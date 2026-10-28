using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PayFlow.IntegrationTests.Support;
using PayFlow.PaymentService.Application;

namespace PayFlow.IntegrationTests.Postgres;

/// <summary>
/// The real HttpPaymentProvider and its resilience pipeline against the real simulator over real HTTP.
/// No database is touched, only the HTTP behaviour: retries, timeouts, idempotency.
/// </summary>
public sealed class ProviderClientTests : IAsyncLifetime
{
    private WebApplicationFactory<PayFlow.PaymentProviderSimulator.ChargeRequest> _simulator = default!;
    private ServiceProvider _services = default!;

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("OTEL_SDK_DISABLED", "true");
        // A real Kestrel server on a loopback port, not the in-memory TestServer: timeouts, cancellation and retries
        // then behave exactly as over a network.
        _simulator = new WebApplicationFactory<PayFlow.PaymentProviderSimulator.ChargeRequest>()
            .WithWebHostBuilder(b => b.UseEnvironment("Development"));
        _simulator.UseKestrel(0);
        _simulator.StartServer();
        using var probe = _simulator.CreateClient();

        _services = await TestServices.PaymentServicesAsync(
            "Host=unused.invalid;Database=unused", provider: null, migrate: false,
            providerBaseUrl: probe.BaseAddress!.ToString());
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _simulator.DisposeAsync();
    }

    private Task<ChargeResult> ChargeAsync(string token, Guid? paymentId = null) =>
        _services.GetRequiredService<IPaymentProvider>()
            .ChargeAsync(new ChargeRequest(paymentId ?? Guid.NewGuid(), 10m, "EUR", token), CancellationToken.None);

    [Theory]
    [InlineData("tok_success", ChargeOutcome.Succeeded)]
    [InlineData("tok_declined", ChargeOutcome.Declined)]
    [InlineData("tok_insufficient", ChargeOutcome.Declined)]
    [InlineData("tok_async", ChargeOutcome.Pending)]
    public async Task MapsProviderAnswersToChargeOutcomes(string token, ChargeOutcome expected) =>
        Assert.Equal(expected, (await ChargeAsync(token)).Outcome);

    [Fact]
    public async Task TransientFailures_AreRetriedUntilTheProviderRecovers()
    {
        // tok_flaky answers 503 twice per idempotency key, then 200: the retry policy must absorb that.
        var result = await ChargeAsync("tok_flaky");

        Assert.Equal(ChargeOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public async Task ProviderThatKeepsFailing_SurfacesAsProviderUnavailable() =>
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => ChargeAsync("tok_500"));

    [Fact]
    public async Task HungProvider_IsCutOffByTheTimeoutPolicyAndSurfacesAsUnavailable()
    {
        // tok_timeout hangs for 30 s; per-attempt (3 s) and total (15 s) timeouts must end the call well before.
        var started = DateTimeOffset.UtcNow;

        await Assert.ThrowsAsync<ProviderUnavailableException>(() => ChargeAsync("tok_timeout"));

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(25), "the timeout policy did not cut the call short");
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Simulator_ExposesTheProbeEndpointsKubernetesUses(string path)
    {
        // The manifests probe /health/live and /health/ready on every service. The simulator once only had /health,
        // so the cluster restarted it forever (HTTP 404 on the startup probe).
        using var client = _simulator.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RepeatingAChargeWithTheSameIdempotencyKey_ReturnsTheSameResult()
    {
        var paymentId = Guid.NewGuid();

        var first = await ChargeAsync("tok_async", paymentId);
        var second = await ChargeAsync("tok_async", paymentId);

        Assert.Equal(first, second); // same provider reference: the provider did not create a second charge
    }
}
