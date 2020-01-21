using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace DynamicCodeExecution
{
    class Program
    {
        static CSharpCompilation GenerateCode(string sourceCode)
        {
            var codeString = SourceText.From(sourceCode);
            var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp7_3);

            var parsedSyntaxTree = SyntaxFactory.ParseSyntaxTree(codeString, options);

            var references = new MetadataReference[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Runtime.AssemblyTargetedPatchBandAttribute).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Microsoft.CSharp.RuntimeBinder.CSharpArgumentInfo).Assembly.Location),
            };

            return CSharpCompilation.Create("DynamicExecute.dll",
                new[] { parsedSyntaxTree },
                references: references,
                options: new CSharpCompilationOptions(OutputKind.ConsoleApplication,
                    optimizationLevel: OptimizationLevel.Release,
                    assemblyIdentityComparer: DesktopAssemblyIdentityComparer.Default));
        }

        internal class SimpleUnloadableAssemblyLoadContext : AssemblyLoadContext
        {
            public SimpleUnloadableAssemblyLoadContext()
                : base(true)
            {
            }

            protected override Assembly Load(AssemblyName assemblyName)
            {
                return null;
            }
        }

        private static WeakReference LoadAndExecute(byte[] compiledAssembly, string[] args)
        {
            using (var asm = new MemoryStream(compiledAssembly))
            {
                var assemblyLoadContext = new SimpleUnloadableAssemblyLoadContext();
                var assembly = assemblyLoadContext.LoadFromStream(asm);

                var entry = assembly.EntryPoint;

                _ = entry != null && entry.GetParameters().Length > 0
                    ? entry.Invoke(null, new object[] { args })
                    : entry.Invoke(null, null);

                assemblyLoadContext.Unload();

                return new WeakReference(assemblyLoadContext);
            }
        }

        static void Main()
        {
            byte[] compiledAssembly;
            using (var peStream = new MemoryStream())
            {
                var sourceCode = File.ReadAllText("Test.cs");
                var result = GenerateCode(sourceCode).Emit(peStream);
                if (!result.Success)
                {
                    Console.WriteLine("Compilation done with error.");

                    var failures = result.Diagnostics
                        .Where(diagnostic => diagnostic.IsWarningAsError || diagnostic.Severity == DiagnosticSeverity.Error);

                    foreach (var diagnostic in failures)
                    {
                        Console.Error.WriteLine("{0}: {1}", diagnostic.Id, diagnostic.GetMessage());
                    }

                    compiledAssembly = null;
                }
                else
                {
                    Console.WriteLine("Compilation done without any error.");
                    peStream.Seek(0, SeekOrigin.Begin);
                    compiledAssembly = peStream.ToArray();
                }
            }

            var assemblyLoadContextWeakRef = LoadAndExecute(compiledAssembly, new[] { "Test 1", "Test 2" });
            for (var i = 0; i < 8 && assemblyLoadContextWeakRef.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Console.WriteLine(assemblyLoadContextWeakRef.IsAlive ? "Unloading failed!" : "Unloading success!");
        }
    }
}
