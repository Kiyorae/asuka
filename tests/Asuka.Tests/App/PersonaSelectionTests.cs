using Asuka.App;
using Asuka.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asuka.Tests.App;

[TestClass]
public sealed class PersonaSelectionTests
{
    private static readonly User First = new("First", id: "first");
    private static readonly User Second = new("Second", id: "second");
    private static readonly User Third = new("Third", id: "third");
    private static readonly User[] Users = [First, Second, Third];

    [TestMethod]
    public void ResolveMissingIdsUsesFirstTwoDistinctUsers()
    {
        var result = PersonaSelection.Resolve(Users, null, null);

        Assert.AreEqual(First.Id, result.Active.Id);
        Assert.AreEqual(Second.Id, result.Bot.Id);
        Assert.AreNotEqual(result.Active.Id, result.Bot.Id);
    }

    [TestMethod]
    public void ResolveInvalidIdsUsesDistinctFallback()
    {
        var result = PersonaSelection.Resolve(Users, "missing-active", "missing-bot");

        Assert.AreEqual(First.Id, result.Active.Id);
        Assert.AreEqual(Second.Id, result.Bot.Id);
    }

    [TestMethod]
    public void ResolveSameValidRequestedIdsPreservesBoth()
    {
        var result = PersonaSelection.Resolve(Users, Second.Id, Second.Id);

        Assert.AreEqual(Second.Id, result.Active.Id);
        Assert.AreEqual(Second.Id, result.Bot.Id);
    }

    [TestMethod]
    public void ResolveValidDistinctRequestedIdsPreservesBoth()
    {
        var result = PersonaSelection.Resolve(Users, Third.Id, Second.Id);

        Assert.AreEqual(Third.Id, result.Active.Id);
        Assert.AreEqual(Second.Id, result.Bot.Id);
    }

    [TestMethod]
    public void ResolveLegacyIdAssistsWhenModernIdsAreMissing()
    {
        var result = PersonaSelection.Resolve(Users, null, null, Third.Id);

        Assert.AreEqual(Third.Id, result.Active.Id);
        Assert.AreEqual(First.Id, result.Bot.Id);
    }
}
