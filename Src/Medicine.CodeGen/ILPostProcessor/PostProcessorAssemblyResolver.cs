using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Unity.CompilationPipeline.Common.ILPostProcessing;
using static System.StringComparison;

namespace Medicine
{
    sealed class PostProcessorReflectionImporter : DefaultReflectionImporter
    {
        private const string SystemPrivateCoreLib = "System.Private.CoreLib";
        private readonly AssemblyNameReference _correctCorlib;

        public PostProcessorReflectionImporter(ModuleDefinition module) : base(module)
            => _correctCorlib = module
                .AssemblyReferences
                .FirstOrDefault(
                    assembly => assembly.Name == "mscorlib" || assembly.Name == "netstandard" || assembly.Name == SystemPrivateCoreLib
                );

        public override AssemblyNameReference ImportReference(AssemblyName reference)
            => _correctCorlib == null || reference.Name != SystemPrivateCoreLib
                ? base.ImportReference(reference)
                : _correctCorlib;
    }

    sealed class PostProcessorReflectionImporterProvider : IReflectionImporterProvider
    {
        public IReflectionImporter GetReflectionImporter(ModuleDefinition module)
            => new PostProcessorReflectionImporter(module);
    }

    sealed class PostProcessorAssemblyResolver : IAssemblyResolver
    {
        private readonly Dictionary<(string filename, DateTime modified), AssemblyDefinition> _cache
            = new Dictionary<(string, DateTime), AssemblyDefinition>();

        private readonly string _compiledAssemblyName;
        private readonly string[] _referenceDirectoryNames;
        private readonly string[] _referenceFileNames;
        private readonly string[] _references;

        private AssemblyDefinition _selfAssembly;

        public PostProcessorAssemblyResolver(ICompiledAssembly compiledAssembly)
        {
            _compiledAssemblyName = compiledAssembly.Name;
            _references = compiledAssembly.References;
            _referenceFileNames = _references.Select(Path.GetFileName).ToArray();
            _referenceDirectoryNames = _references.Select(Path.GetDirectoryName).Distinct().ToArray();
        }

        public AssemblyDefinition Resolve(AssemblyNameReference name)
            => Resolve(name, new ReaderParameters(ReadingMode.Deferred));

        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            if (name.Name == _compiledAssemblyName)
                return _selfAssembly;

            string filename = FindFile(name).Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(filename))
                return null;

            var cacheKey = (fileName: filename, File.GetLastWriteTime(filename));

            if (_cache.TryGetValue(cacheKey, out var result))
                return result;

            parameters.AssemblyResolver = this;

            var ms = MemoryStreamFor(filename);

            string candidate1 = filename + ".pdb";
            string candidate2 = filename.Substring(filename.Length - 4) + ".pdb";

            if (File.Exists(candidate1))
                parameters.SymbolStream = MemoryStreamFor(candidate1);
            else if (File.Exists(candidate2))
                parameters.SymbolStream = MemoryStreamFor(candidate2);

            var assemblyDefinition = AssemblyDefinition.ReadAssembly(ms, parameters);
            _cache.Add(cacheKey, assemblyDefinition);
            return assemblyDefinition;
        }

        void IDisposable.Dispose() { }

        public static AssemblyDefinition GetAssemblyDefinitionFor(ICompiledAssembly compiledAssembly)
        {
            var resolver = new PostProcessorAssemblyResolver(compiledAssembly);

            var assemblyDefinition = AssemblyDefinition.ReadAssembly(
                stream: new MemoryStream(compiledAssembly.InMemoryAssembly.PeData),
                parameters: new ReaderParameters
                {
                    SymbolStream = new MemoryStream(compiledAssembly.InMemoryAssembly.PdbData),
                    SymbolReaderProvider = new PortablePdbReaderProvider(),
                    AssemblyResolver = resolver,
                    ReflectionImporterProvider = new PostProcessorReflectionImporterProvider(),
                    ReadingMode = ReadingMode.Immediate,
                }
            );

            resolver._selfAssembly = assemblyDefinition;

            return assemblyDefinition;
        }

        public string FindFile(AssemblyNameReference name)
            => FindFile(name.Name);

        public string FindFile(string name)
        {
            string nameString = name;
            string nameStringDll = nameString + ".dll";
            string nameStringExe = nameString + ".exe";

            for (var i = 0; i < _referenceFileNames.Length; i++)
            {
                string referenceFileName = _referenceFileNames[i];

                if (referenceFileName == nameStringDll)
                    return _references[i];
                if (referenceFileName == nameStringExe)
                    return _references[i];
            }

            foreach (var parentDir in _referenceDirectoryNames)
            {
                string candidate = Path.Combine(parentDir, nameStringDll);
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        static MemoryStream MemoryStreamFor(string fileName)
            => Retry(
                retryCount: 10,
                waitTime: TimeSpan.FromSeconds(1),
                func: () =>
                {
                    using (var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        var byteArray = new byte[fs.Length];
                        var readLength = fs.Read(byteArray, 0, (int)fs.Length);
                        if (readLength != fs.Length)
                            throw new InvalidOperationException("File read length is not full length of file.");

                        return new MemoryStream(byteArray);
                    }
                });

        static MemoryStream Retry(int retryCount, in TimeSpan waitTime, Func<MemoryStream> func)
        {
            try
            {
                return func();
            }
            catch (IOException)
            {
                if (retryCount == 0)
                    throw;

                Console.WriteLine($"Caught IO Exception, trying {retryCount} more times");
                Thread.Sleep(waitTime);
                return Retry(retryCount - 1, waitTime, func);
            }
        }
    }
}
