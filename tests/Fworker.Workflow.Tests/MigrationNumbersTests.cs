using Fworker.Workflows.Merge;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>W9.10q: <c>Get-MigrationCollision</c> (Prs.ps1) as a pure rule: the cases of the script, one by one.</summary>
public class MigrationNumbersTests
{
    const string Dir = "src/Database/scripts/Update/";

    static (string, string) Added(string name) => (Dir + name, "added");

    [Fact]
    public void AScriptWhoseNumberADifferentScriptOnTheBaseBranchHas_IsACollision_WithTheNextFreeNumber()
    {
        var c = MigrationNumbers.Find([Added("040_AddOrders.sql")], MigrationNumbers.ScriptsOnBase(["039_A.sql", "040_Other.sql", "notes.txt"]))!;
        Assert.Equal(new MigrationCollision(Dir + "040_AddOrders.sql", Dir + "040_Other.sql", "041"), c);
    }

    [Fact]
    public void TheNextNumberIsOneAboveTheHighestOfTheBaseBranchAndThePullRequest_PaddedAsTheScriptsNumber()
    {
        var c = MigrationNumbers.Find([Added("0040_Mine.sql"), Added("0045_Second.sql")], MigrationNumbers.ScriptsOnBase(["0040_Other.sql", "0043_C.sql"]))!;
        Assert.Equal("0046", c.Next); Assert.Equal(Dir + "0040_Mine.sql", c.Script);
    }

    [Fact]
    public void TheSameScriptOnBothSides_ARemovedScript_AndAScriptOutsideTheDirectory_AreNoCollision()
    {
        var onBase = MigrationNumbers.ScriptsOnBase(["040_Same.sql", "041_Other.sql"]).ToList();
        Assert.Null(MigrationNumbers.Find([Added("040_Same.sql")], onBase));                                                 // the script itself, already there
        Assert.Null(MigrationNumbers.Find([(Dir + "041_Gone.sql", "removed")], onBase));                                     // removed by the pull request
        Assert.Null(MigrationNumbers.Find([("src/Other/041_Elsewhere.sql", "added"), (Dir + "sub/041_Deep.sql", "added")], onBase));   // not in the directory
        Assert.Null(MigrationNumbers.Find([Added("042_New.sql")], onBase));
        Assert.Null(MigrationNumbers.Find([Added("041_Renamed.sql"), (Dir + "041_Other.sql", "renamed")], onBase));           // both of them are the pull request's: the base's script moves with it
    }

    [Fact]
    public void ARenamedScript_ThatTakesAnotherScriptsNumber_IsACollision_AndTheMatchIsWithoutRegardToCase()
    {
        var c = MigrationNumbers.Find([(Dir + "040_Renamed.SQL", "renamed")], MigrationNumbers.ScriptsOnBase(["040_Old.sql"]))!;
        Assert.Equal(Dir + "040_Old.sql", c.TakenBy); Assert.Equal("041", c.Next);
        Assert.True(MigrationNumbers.Touches([(Dir + "040_Renamed.SQL", "renamed")]));
        Assert.False(MigrationNumbers.Touches([(Dir + "040_Gone.sql", "removed"), ("README.md", "modified")]));
    }

    [Fact]
    public void AnEmptyDirectoryOnTheBaseBranch_HasNoCollision_AndTheFirstPairIsReported()
    {
        Assert.Null(MigrationNumbers.Find([Added("001_First.sql")], []));
        var c = MigrationNumbers.Find([Added("002_B.sql"), Added("001_A.sql")], MigrationNumbers.ScriptsOnBase(["001_X.sql", "002_Y.sql"]))!;
        Assert.Equal(Dir + "002_B.sql", c.Script);   // the pull request's own order
    }

    [Fact]
    public void TheRefusalsTextsAreTheScriptsOwn()
    {
        var c = new MigrationCollision(Dir + "040_A.sql", Dir + "040_B.sql", "041");
        Assert.Equal("merge refused: PR #12 adds src/Database/scripts/Update/040_A.sql, but src/Database/scripts/Update/040_B.sql on master already has that number; the branch's script must be renumbered to 041 (never the one on master: DbUp runs a renamed script again)", MigrationNumbers.MergeReason(12, c, "master"));
        Assert.Equal("the pull request adds src/Database/scripts/Update/040_A.sql, but src/Database/scripts/Update/040_B.sql on master already has that number. Renumber this branch's script to 041 (never rename the script on master: DbUp runs a renamed script again), push, then call complete again.", MigrationNumbers.CompleteReason(c, "master"));
    }
}
