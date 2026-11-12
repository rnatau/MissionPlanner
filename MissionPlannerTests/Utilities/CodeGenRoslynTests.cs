using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MissionPlanner;

namespace MissionPlannerTests.Utilities
{
    [TestClass]
    [DoNotParallelize] // CodeGenRoslyn.CachePath is application-wide configuration.
    public class CodeGenRoslynTests
    {
        private string directory;
        private string source;
        private string originalCache;
        private const string Code = @"using System.Diagnostics;
using System.Runtime.CompilerServices;
public static class Probe {
 [MethodImpl(MethodImplOptions.NoInlining)]
 public static string Capture() { var frame = new StackTrace(0, true).GetFrame(0); return frame.GetFileName() + "":"" + frame.GetFileLineNumber(); }
}";

        [TestInitialize]
        public void Initialize()
        {
            originalCache = CodeGenRoslyn.CachePath;
            directory = Path.Combine(Path.GetTempPath(), "mp-plugin-symbols-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            CodeGenRoslyn.CachePath = directory;
            source = Path.Combine(directory, "Probe_" + Guid.NewGuid().ToString("N") + ".cs");
            File.WriteAllText(source, Code, new UTF8Encoding(false));
        }

        [TestCleanup]
        public void Cleanup()
        {
            CodeGenRoslyn.CachePath = originalCache;
            Directory.Delete(directory, true);
        }

        private void CheckLocations(Assembly assembly)
        {
            Assert.IsNotNull(assembly, CodeGenRoslyn.lasterror);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var trace = (string)assembly.GetType("Probe").GetMethod("Capture").Invoke(null, null);
            Assert.AreEqual(source + ":5", trace, "File and line logging must remain usable across runtimes.");
        }

        [TestMethod]
        public void CompiledAndCachedPluginsRetainSourceLocations()
        {
            CheckLocations(CodeGenRoslyn.BuildCode(source));
            // Warm compiler dependencies so the reference snapshot is stable.
            CheckLocations(CodeGenRoslyn.BuildCode(source));
            var files = Directory.GetFiles(directory, "*.dll");
            var versions = files.Select(File.GetLastWriteTimeUtc).ToArray();
            CheckLocations(CodeGenRoslyn.BuildCode(source));
            CollectionAssert.AreEqual(files, Directory.GetFiles(directory, "*.dll"));
            CollectionAssert.AreEqual(versions, files.Select(File.GetLastWriteTimeUtc).ToArray(), "A cache hit must not recompile.");
            Assert.AreEqual(0, Directory.GetFiles(directory, "*.pdb").Length, "Symbols belong to the cached assembly.");
        }

        [TestMethod]
        public void IdenticalSourceAtDifferentPathsKeepsItsOwnDebugDocument()
        {
            CheckLocations(CodeGenRoslyn.BuildCode(source));
            var firstSource = source;
            source = Path.Combine(directory, "MovedProbe.cs");
            File.WriteAllText(source, Code, new UTF8Encoding(false));
            CheckLocations(CodeGenRoslyn.BuildCode(source));
            CheckLocations(CodeGenRoslyn.BuildCode(source));
            source = firstSource;
            CheckLocations(CodeGenRoslyn.BuildCode(source));
        }

        [TestMethod]
        public void LegacyTruncatedSymbolsCannotReachTheNativeRuntime()
        {
            // Cover both legacy key formats, including upstream's host-build
            // salt, with a PDB truncated as in the captured SIGABRT. How the
            // original cached file became corrupt was not established.
            var buildId = (Assembly.GetEntryAssembly() ?? typeof(CodeGenRoslyn).Assembly)
                .ManifestModule.ModuleVersionId.ToString();
            var compilation = CSharpCompilation.Create(Path.GetFileNameWithoutExtension(source),
                new[] { CSharpSyntaxTree.ParseText(Code, path: source, encoding: Encoding.UTF8) },
                new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using (var dll = new MemoryStream())
            using (var pdb = new MemoryStream())
            {
                Assert.IsTrue(compilation.Emit(dll, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb)).Success);
                foreach (var identity in new[] { Code, Code + buildId })
                {
                    string key;
                    using (var hash = MD5.Create())
                        key = BitConverter.ToString(hash.ComputeHash(identity.Select(c => (byte)c).ToArray()))
                            .Replace("-", "").ToLowerInvariant();
                    File.WriteAllBytes(Path.Combine(directory, key + ".dll"), dll.ToArray());
                    File.WriteAllBytes(Path.Combine(directory, key + ".pdb"), pdb.ToArray().Take((int)pdb.Length - 8).ToArray());
                }
            }
            CheckLocations(CodeGenRoslyn.BuildCode(source));
            CheckLocations(CodeGenRoslyn.BuildCode(source));
        }

        [TestMethod]
        public void TruncatedCacheIsRebuiltWithWorkingSymbols()
        {
            CheckLocations(CodeGenRoslyn.BuildCode(source));
            CheckLocations(CodeGenRoslyn.BuildCode(source));
            foreach (var file in Directory.GetFiles(directory, "*.dll"))
            {
                var bytes = File.ReadAllBytes(file);
                File.WriteAllBytes(file, bytes.Take(bytes.Length / 2).ToArray());
            }
            CheckLocations(CodeGenRoslyn.BuildCode(source));
            CheckLocations(CodeGenRoslyn.BuildCode(source));
        }

        [TestMethod]
        public void ConcurrentCompilersPublishCompleteAssemblies()
        {
            CheckLocations(CodeGenRoslyn.BuildCode(source));
            CheckLocations(CodeGenRoslyn.BuildCode(source));
            foreach (var file in Directory.GetFiles(directory, "*.dll")) File.Delete(file);
            using (var ready = new CountdownEvent(6))
            using (var start = new ManualResetEventSlim())
            {
                var workers = Enumerable.Range(0, 6).Select(_ => Task.Factory.StartNew(() =>
                {
                    ready.Signal();
                    start.Wait();
                    return CodeGenRoslyn.BuildCode(source);
                }, TaskCreationOptions.LongRunning)).ToArray();
                Assert.IsTrue(ready.Wait(TimeSpan.FromSeconds(10)));
                start.Set();
                Assert.IsTrue(Task.WaitAll(workers, TimeSpan.FromSeconds(30)));
                foreach (var worker in workers) CheckLocations(worker.Result);
            }
            CheckLocations(CodeGenRoslyn.BuildCode(source));
            Assert.AreEqual(0, Directory.GetFiles(directory, "*.tmp").Length);
        }
    }
}
