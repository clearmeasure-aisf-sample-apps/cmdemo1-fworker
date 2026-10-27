using Xunit;

namespace Fworker.Workflow.Tests;

public class ProjectTests
{
    [Fact]
    public void WorkflowsSeeContractsAndDomainThroughTheirReferences()
    {
        Assert.True(typeof(Fworker.Contracts.Seams.ICommandPort).IsInterface);
        Assert.Equal("Fworker.Domain", typeof(Fworker.Domain.ProjectMarker).Assembly.GetName().Name);
    }
}
