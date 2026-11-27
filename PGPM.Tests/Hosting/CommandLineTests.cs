namespace PGPM.Tests.Hosting;

/// <summary>`pgpm …` (docs/03 §3.1): the commands are told apart from the server's own arguments.</summary>
[TestClass]
public sealed class CommandLineTests
{
    [TestMethod]
    [DataRow("db", true)]
    [DataRow("help", true)]
    [DataRow("--urls", false)]
    [DataRow("serve", false)]
    public void KnowsItsCommands(string arg, bool isCommand) => Assert.AreEqual(isCommand, Cli.CommandLine.IsCommand(arg));

    [TestMethod]
    public void AnUnknownSubcommandIsAUsageError() => Assert.AreEqual(2, Cli.CommandLine.Run(["db", "drop-everything"]));
}
