using System.Diagnostics;

// The environment of every pwsh process the gateway starts (a rule-script call of Runner, a worker of RunnerPool).
// The pwsh processes of the gateway share no .NET startup profile ($HOME/.cache/powershell/StartupProfileData-*): concurrent or killed writers
// corrupt it, and every later pwsh that reads it dies with SIGSEGV, exit 139 (issue 375). Multicore JIT is off for them: no read, no write.

/// <summary>Sets the environment of a pwsh process the gateway starts.</summary>
static class RunnerEnv
{
    /// <summary>The .NET runtime setting that turns multicore JIT off when the machine has fewer processors than its value.</summary>
    public const string Knob = "DOTNET_MultiCoreJitMinNumCpus";

    /// <summary>The value of <see cref="Knob"/> that no machine reaches: multicore JIT is off, no startup profile is read or written.</summary>
    public const string Off = "65535";

    /// <summary>Turns multicore JIT off for the process of <paramref name="psi"/>, unless the gateway's own environment already sets <see cref="Knob"/>.</summary>
    /// <param name="psi">The start info of the pwsh process, before it starts.</param>
    public static void Apply(ProcessStartInfo psi)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Knob))) psi.Environment[Knob] = Off;
    }
}
