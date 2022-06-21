using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using JetBrains.Annotations;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Unity.CompilationPipeline.Common.Diagnostics;
using Unity.CompilationPipeline.Common.ILPostProcessing;
using static System.StringComparison;
using Debug = UnityEngine.Debug;

namespace Medicine
{
    [UsedImplicitly]
    internal sealed class MedicineILPostProcessor : ILPostProcessor
    {
        public override ILPostProcessor GetInstance()
            => this;

        [Conditional("MEDICINE_IL_DEBUG")]
        static void Log(object message)
            => Debug.Log(message);

        public override bool WillProcess(ICompiledAssembly compiledAssembly)
        {
            var compiledAssemblyName = compiledAssembly.Name;

#if MEDICINE_DISABLE
            return false;
#endif

            switch (compiledAssemblyName)
            {
                case "Unity.Medicine.CodeGen":
                case "Medicine":
                    return false;
                case "Assembly-CSharp":
                case "Assembly-CSharp-firstpass":
                    return true;
            }

            foreach (var reference in compiledAssembly.References)
                if (reference.EndsWith("Medicine.dll", Ordinal))
                    return true;

            return false;
        }

        public ILPostProcessResult PostProcessInternal(ICompiledAssembly compiledAssembly)
        {
#if MEDICINE_IL_DEBUG
            using (NonAlloc.Benchmark.Start($"GetAssemblyDefinition ({compiledAssembly.Name})"))
#endif
            var assemblyDefinition = PostProcessorAssemblyResolver.GetAssemblyDefinitionFor(compiledAssembly);

            try
            {
                CecilExtensions.CurrentModule = assemblyDefinition.MainModule;
                PostProcessorContext context;
#if MEDICINE_IL_DEBUG
                using (NonAlloc.Benchmark.Start($"CreatePostProcessorContext ({compiledAssembly.Name})"))
#endif
                context = new PostProcessorContext(assemblyDefinition.MainModule);

#if MEDICINE_IL_DEBUG
                using (NonAlloc.Benchmark.Start($"MedicineInjection ({compiledAssembly.Name})"))
#endif
                new InjectionPostProcessor(context).ProcessAssembly();

                var pe = new MemoryStream(capacity: 1024 * 64);
                var pdb = new MemoryStream(capacity: 1024 * 16);

                var writerParameters = new WriterParameters
                {
                    SymbolWriterProvider = new PortablePdbWriterProvider(),
                    SymbolStream = pdb,
                    WriteSymbols = true,
                };

                assemblyDefinition.Write(pe, writerParameters);
                var inMemoryAssembly = new InMemoryAssembly(pe.ToArray(), pdb.ToArray());

                return new ILPostProcessResult(inMemoryAssembly, context.DiagnosticMessages);
            }
            catch (Exception ex)
            {
                var error = new DiagnosticMessage
                {
                    MessageData = $"Unexpected exception while post-processing assembly {compiledAssembly.Name}:\n{ex}",
                    DiagnosticType = DiagnosticType.Error,
                };
                return new ILPostProcessResult(compiledAssembly.InMemoryAssembly, new List<DiagnosticMessage> {error});
            }
            finally
            {
                CecilExtensions.CurrentModule.Dispose();
                CecilExtensions.CurrentModule = null;
            }
        }

        public override ILPostProcessResult Process(ICompiledAssembly compiledAssembly)
        {
            try
            {
                if (!WillProcess(compiledAssembly))
                    return null;

#if MEDICINE_IL_DEBUG
                using (NonAlloc.Benchmark.Start($"PostProcessInternal ({compiledAssembly.Name})"))
#endif
                return PostProcessInternal(compiledAssembly);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                return null;
            }
        }
    }
}