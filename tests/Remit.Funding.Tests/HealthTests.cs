using System.Net;

namespace Remit.Funding.Tests;

public class HealthTests(InMemoryApiFactory factory) : IClassFixture<InMemoryApiFactory>
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Probes_answer_200_and_need_no_idempotency_key(string path)
    {
        var response = await factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
