using System.Reflection;
using NetArchTest.Rules;
using Remit.BuildingBlocks;

namespace Remit.Architecture.Tests;

/// <summary>
/// The dependency directions drawn in ARCHITECTURE.md ("Components"), checked on the compiled
/// assemblies. Each failure names the offending types and says how to fix the violation.
/// Change a rule only together with ARCHITECTURE.md.
/// </summary>
public class ArchitectureRules
{
    private static readonly Assembly BuildingBlocks = typeof(Money).Assembly;
    private static readonly Assembly Funding = typeof(FundingApp).Assembly;
    private static readonly Assembly Ledger = typeof(LedgerApp).Assembly;
    private static readonly Assembly Reconciliation = typeof(ReconciliationApp).Assembly;

    private static readonly string[] Infrastructure =
    [
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "RabbitMQ",
        "Microsoft.AspNetCore",
        "System.Net.Http",
        "Countersign",
        "OpenTelemetry",
    ];

    private static readonly string[] ServiceNames = ["Remit.Funding", "Remit.Ledger", "Remit.Reconciliation"];

    public static TheoryData<string> Services => new(ServiceNames);

    [Fact]
    public void Building_blocks_do_not_depend_on_any_service()
    {
        var result = Types.InAssembly(BuildingBlocks)
            .ShouldNot().HaveDependencyOnAny("Remit.Funding", "Remit.Ledger", "Remit.Reconciliation")
            .GetResult();

        AssertPasses(result,
            "Remit.BuildingBlocks must not depend on any service.",
            "Building blocks are shared by every service. Move the service-specific code into that service, " +
            "or make the building block generic and let the service pass in what it needs.");
    }

    [Theory]
    [MemberData(nameof(Services))]
    public void A_service_does_not_depend_on_another_service(string service)
    {
        var others = ServiceNames.Where(s => s != service).ToArray();

        var result = Types.InAssembly(AssemblyOf(service))
            .ShouldNot().HaveDependencyOnAny(others)
            .GetResult();

        AssertPasses(result,
            $"{service} must not depend on {string.Join(" or ", others)}.",
            "Services talk only through RabbitMQ events (outbox → relay → consumer with an inbox) or over HTTP " +
            "(see HttpLedgerBalances). Copy the fields you need into a payload record in your own service " +
            "instead of referencing the other service's types or project.");
    }

    [Fact]
    public void Deposit_and_Withdrawal_state_machines_have_no_infrastructure()
    {
        var funding = Types.InAssembly(Funding)
            .That().ResideInNamespaceMatching(@"^Remit\.Funding\.(Deposits|Withdrawals)$")
            .And().HaveNameMatching(@"^(Invalid)?(Deposit|Withdrawal)(Status|Transition|TransitionException)?$")
            .ShouldNot().HaveDependencyOnAny([.. Infrastructure, "Remit.Funding.Persistence", "Remit.Funding.Psp", "Remit.Funding.Messaging"])
            .GetResult();

        AssertPasses(funding,
            "Deposit and Withdrawal (the state machines) must not depend on infrastructure or on other Funding folders.",
            "Keep the state machines plain C#: transitions, rules and Money only. Do database work in " +
            "Persistence/ (or a repository), PSP calls in Psp/, HTTP in the *Endpoints classes, and pass " +
            "results into the aggregate as values.");
    }

    [Fact]
    public void Journal_entries_have_no_infrastructure()
    {
        var ledger = Types.InAssembly(Ledger)
            .That().ResideInNamespaceMatching(@"^Remit\.Ledger$")
            .ShouldNot().HaveDependencyOnAny([.. Infrastructure, "Remit.Ledger.Persistence", "Remit.Ledger.Consuming", "Remit.Ledger.Balances"])
            .GetResult();

        AssertPasses(ledger,
            "JournalEntry and Posting (namespace Remit.Ledger) must not depend on infrastructure or on other Ledger folders.",
            "The journal entry proves it balances in plain C#. Map it to JournalEntryRecord in Persistence/ " +
            "and build it from messages in Consuming/; never the other way round.");
    }

    [Fact]
    public void Statement_matching_is_pure()
    {
        var matching = Types.InAssembly(Reconciliation)
            .That().ResideInNamespace("Remit.Reconciliation.Matching")
            .ShouldNot().HaveDependencyOnAny([
                .. Infrastructure,
                "Remit.Reconciliation.Persistence",
                "Remit.Reconciliation.Consuming",
                "Remit.Reconciliation.Statements",
                "Remit.Reconciliation.Sweeps",
            ])
            .GetResult();

        AssertPasses(matching,
            "Remit.Reconciliation.Matching must stay pure: no I/O, no infrastructure, no other Reconciliation folders.",
            "Load movements and statements in Statements/ or Persistence/, convert them to ExpectedMovement and " +
            "StatementLine, and pass those into StatementMatcher.Match.");
    }

    [Fact]
    public void Money_has_no_infrastructure()
    {
        var money = Types.InAssembly(BuildingBlocks)
            .That().ResideInNamespaceMatching(@"^Remit\.BuildingBlocks$")
            .ShouldNot().HaveDependencyOnAny(Infrastructure)
            .GetResult();

        AssertPasses(money,
            "Money and IUnitOfWork (namespace Remit.BuildingBlocks) must not depend on infrastructure.",
            "Put infrastructure code in a sub-namespace (Hosting, Messaging, Telemetry, Idempotency) or in the service.");
    }

    [Fact]
    public void Funding_endpoints_and_psp_code_use_repositories_not_the_database()
    {
        var result = Types.InAssembly(Funding)
            .That().ResideInNamespace("Remit.Funding.Deposits")
            .Or().ResideInNamespace("Remit.Funding.Withdrawals")
            .Or().ResideInNamespace("Remit.Funding.Psp")
            .ShouldNot().HaveDependencyOnAny("Remit.Funding.Persistence", "Microsoft.EntityFrameworkCore", "Npgsql")
            .GetResult();

        AssertPasses(result,
            "Deposits/, Withdrawals/ and Psp/ must not use FundingDbContext, EF Core or Npgsql directly.",
            "Go through IDepositRepository, IWithdrawalRepository, IOutbox and IUnitOfWork, and put the EF " +
            "implementation in Persistence/ (like EfDepositRepository in PostgresStores.cs). That keeps the " +
            "in-memory mode and the fast tests working.",
            // Known debt (docs/tech-debt.md): the EF withdrawal repository still lives next to the aggregate.
            "Remit.Funding.Withdrawals.EfWithdrawalRepository");
    }

    [Fact]
    public void Funding_persistence_does_not_depend_on_psp_or_messaging()
    {
        var result = Types.InAssembly(Funding)
            .That().ResideInNamespace("Remit.Funding.Persistence")
            .ShouldNot().HaveDependencyOnAny("Remit.Funding.Psp", "Remit.Funding.Messaging")
            .GetResult();

        AssertPasses(result,
            "Remit.Funding.Persistence must not depend on Psp/ or Messaging/.",
            "Persistence stores aggregates, the outbox and idempotency keys. The relay in Messaging/ reads the " +
            "outbox, not the other way round; provider calls stay in Psp/.");
    }

    [Theory]
    [MemberData(nameof(Services))]
    public void Only_the_messaging_building_block_talks_to_RabbitMQ(string service)
    {
        var inService = Types.InAssembly(AssemblyOf(service))
            .ShouldNot().HaveDependencyOn("RabbitMQ")
            .GetResult();

        AssertPasses(inService,
            $"{service} must not use RabbitMQ.Client directly.",
            "Publish by writing to the outbox (IOutbox) and consume by implementing IMessageHandler; " +
            "RabbitMqPublisher and RabbitMqConsumer in Remit.BuildingBlocks.Messaging own the broker, " +
            "the trace headers and the dead-lettering.");
    }

    [Fact]
    public void RabbitMQ_stays_inside_the_messaging_namespace_of_the_building_blocks()
    {
        var result = Types.InAssembly(BuildingBlocks)
            .That().DoNotResideInNamespace("Remit.BuildingBlocks.Messaging")
            .ShouldNot().HaveDependencyOn("RabbitMQ")
            .GetResult();

        AssertPasses(result,
            "Only Remit.BuildingBlocks.Messaging may use RabbitMQ.Client.",
            "Move the broker code into Remit.BuildingBlocks.Messaging and expose an abstraction such as IMessagePublisher.");
    }

    [Fact]
    public void Webhook_signature_verification_stays_in_the_psp_boundary()
    {
        var result = Types.InAssembly(Funding)
            .That().DoNotResideInNamespace("Remit.Funding.Psp")
            .ShouldNot().HaveDependencyOn("Countersign")
            .GetResult();

        AssertPasses(result,
            "Only Remit.Funding.Psp may use Countersign.",
            "Verify provider webhooks in Psp/WebhookEndpoints.cs before anything is parsed (ADR-0006), " +
            "and hand the verified result to the aggregates.");
    }

    private static Assembly AssemblyOf(string service) => service switch
    {
        "Remit.Funding" => Funding,
        "Remit.Ledger" => Ledger,
        "Remit.Reconciliation" => Reconciliation,
        _ => throw new ArgumentOutOfRangeException(nameof(service)),
    };

    private static void AssertPasses(TestResult result, string rule, string fix, params string[] allowed)
    {
        var violations = (result.FailingTypeNames ?? [])
            .Where(name => !allowed.Any(a => name.StartsWith(a, StringComparison.Ordinal)))
            .ToList();

        Assert.True(violations.Count == 0,
            $"Architecture rule broken: {rule}{Environment.NewLine}" +
            $"Types:{Environment.NewLine}  - {string.Join($"{Environment.NewLine}  - ", violations)}{Environment.NewLine}" +
            $"How to fix: {fix}{Environment.NewLine}" +
            "If the rule itself is wrong, change it together with ARCHITECTURE.md in the same PR.");
    }
}
