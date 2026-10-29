using Fworker.Database;
using Fworker.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// EF Core is a query mapper here: the schema is built by the scripts of Fworker.Database. This test is the guard that used to be HasPendingModelChanges: a database built by the scripts is compared with the
/// database EF itself would create from FactoryDbContext (EnsureCreated, allowed in tests only). A change of the model without a new script fails here.
/// </summary>
public class SchemaMatchesEfModelTests
{
    // the tables the gateway's SQL stores use without EF (raw SQL), so no entity maps them
    static readonly string[] NotMapped = ["IdempotencyLedger", "CopilotReservations", "IngressDedup"];

    [Fact]
    public async Task TheScriptsBuildTheEfModel()
    {
        if (!TestDatabase.Available) return;
        await using var scripts = await TestDatabase.CreateAsync("fw_efa_");
        await using var ef = await TestDatabase.CreateEmptyAsync("fw_efb_");
        FactoryDbContext Open(string cs) => new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(cs).Options);
        await using (var db = Open(ef.ConnectionString)) Assert.True(await db.Database.EnsureCreatedAsync());

        var built = await SchemaManifest.ReadAsync(scripts.ConnectionString);
        var model = await SchemaManifest.ReadAsync(ef.ConnectionString);
        await using var context = Open(ef.ConnectionString);
        var mapped = context.Model.GetEntityTypes().Select(t => $"{t.GetSchema()}.{t.GetTableName()}").Order().ToList();
        Assert.NotEmpty(mapped);
        Assert.Equal(mapped, model.Tables.Keys.Where(k => k.StartsWith("factory.", StringComparison.Ordinal)).Order().ToList());   // EF made exactly the mapped tables

        var problems = new List<string>();
        foreach (var table in mapped)
        {
            if (!built.Tables.TryGetValue(table, out var have)) { problems.Add("the scripts build no table " + table); continue; }
            var want = model.Tables[table];
            foreach (var (name, col) in want.Columns)
            {
                // the default of a column is left out: the model declares none where script 005 gives old rows one
                if (!have.Columns.TryGetValue(name, out var c)) problems.Add($"{table}.{name} is missing");
                else if (c with { Default = null } != col with { Default = null }) problems.Add($"{table}.{name} differs: scripts {c}, model {col}");
            }
            problems.AddRange(have.Columns.Keys.Where(k => !want.Columns.ContainsKey(k)).Select(k => $"{table}.{k} is in the scripts, not in the model"));
            foreach (var (name, ix) in want.Indexes)
            {
                if (!have.Indexes.TryGetValue(name, out var i)) problems.Add($"{table} index {name} is missing");
                else if (!i.SameAs(ix)) problems.Add($"{table} index {name} differs: scripts {Describe(i)}, model {Describe(ix)}");
            }
            problems.AddRange(have.Indexes.Keys.Where(k => !want.Indexes.ContainsKey(k)).Select(k => $"{table} index {k} is in the scripts, not in the model"));
        }
        Assert.True(problems.Count == 0, "the scripts of Fworker.Database and FactoryDbContext disagree (a model change needs a new script): " + string.Join("; ", problems));

        // every table of the scripts in the gateway's schema is mapped, or is one of the three the raw-SQL stores use
        var unmapped = built.Tables.Keys.Where(k => k.StartsWith("factory.", StringComparison.Ordinal)).Except(mapped).Select(k => k["factory.".Length..]).Order().ToList();
        Assert.Equal(NotMapped.Order(), unmapped);
    }

    static string Describe(IndexInfo i) => $"{(i.Unique ? "unique " : "")}({string.Join(",", i.Keys)}){(i.Filter == null ? "" : " where " + i.Filter)}";
}
