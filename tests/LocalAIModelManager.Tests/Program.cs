using System.Text;

namespace LocalAIModelManager.Tests;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("Local AI Model Manager - offline test + acceptance harness");
        Console.WriteLine($"mock engine: {TestWorkspace.MockEnginePath}");

        if (!File.Exists(TestWorkspace.MockEnginePath))
        {
            Console.Error.WriteLine($"FATAL: the mock engine was not copied to the test output: {TestWorkspace.MockEnginePath}");
            return 2;
        }

        var filter = args.FirstOrDefault(a => !a.StartsWith('-'));

        UnitTests.Register();
        AcceptanceTests.Register();

        return await TestFramework.RunAllAsync(filter).ConfigureAwait(false);
    }
}
