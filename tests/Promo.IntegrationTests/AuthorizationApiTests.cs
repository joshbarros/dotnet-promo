using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Promo.Redemptions;
using Promo.Coupons;
using Xunit;

namespace Promo.IntegrationTests;

[Collection("api")]
public class AuthorizationApiTests(ApiFactory factory)
{
    [Theory]
    [InlineData("/api/campaigns")]
    [InlineData("/api/redemptions")]
    [InlineData("/api/coupons/11111111-1111-1111-1111-111111111111")]
    [InlineData("/api/redemptions/11111111-1111-1111-1111-111111111111/events")]
    public async Task EverythingButHealthAndAuth_RequiresASignedInUser(string path) =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.NewClient().GetAsync(path)).StatusCode);

    [Fact]
    public async Task WritesRequireASignedInUser()
    {
        var client = factory.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/campaigns", new { code = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/redemptions", new { campaignCode = "X" })).StatusCode);
    }

    [Fact]
    public async Task Health_IsOpen_AndReadinessChecksTheDatabases()
    {
        var client = factory.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health/ready")).StatusCode);
    }

    [Fact]
    public async Task ACustomer_CannotOpenACampaign_ButAnAdministratorCan()
    {
        var customer = await factory.RegisterAsync();
        var admin = await factory.AdminAsync();
        var body = ApiHelpers.CampaignBody(ApiHelpers.NewCode(), 10);

        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(customer.AccessToken).PostAsJsonAsync("/api/campaigns", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await factory.ClientFor(admin.AccessToken).PostAsJsonAsync("/api/campaigns", body)).StatusCode);
    }

    [Fact]
    public async Task ACustomer_CanReadTheCatalog()
    {
        var admin = await factory.AdminAsync();
        var code = await factory.CreateCampaignAsync(admin);
        var customer = await factory.RegisterAsync();

        var campaign = await factory.ClientFor(customer.AccessToken).GetAsync($"/api/campaigns/{code}");
        Assert.Equal(HttpStatusCode.OK, campaign.StatusCode);
    }

    [Fact]
    public async Task ARedemption_BelongsToItsOwner()
    {
        var admin = await factory.AdminAsync();
        var campaign = await factory.CreateCampaignAsync(admin);
        var ana = await factory.RegisterAsync();
        var bruno = await factory.RegisterAsync();
        var id = await factory.BookAsync(ana, campaign);

        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(ana.AccessToken).GetAsync($"/api/redemptions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(admin.AccessToken).GetAsync($"/api/redemptions/{id}")).StatusCode);
        // Another customer learns nothing, not even that the id exists.
        Assert.Equal(HttpStatusCode.NotFound, (await factory.ClientFor(bruno.AccessToken).GetAsync($"/api/redemptions/{id}")).StatusCode);
    }

    [Fact]
    public async Task AnotherCustomer_CannotRevokeSomeoneElsesRedemption()
    {
        var admin = await factory.AdminAsync();
        var campaign = await factory.CreateCampaignAsync(admin);
        var ana = await factory.RegisterAsync();
        var bruno = await factory.RegisterAsync();
        var id = await factory.BookAsync(ana, campaign);

        var attempt = await factory.ClientFor(bruno.AccessToken).PostAsync($"/api/redemptions/{id}/revocation", null);
        Assert.Equal(HttpStatusCode.NotFound, attempt.StatusCode);
    }

    [Fact]
    public async Task TheList_ShowsACustomerOnlyTheirOwnRedemptions_AndStaffEverything()
    {
        var admin = await factory.AdminAsync();
        var campaign = await factory.CreateCampaignAsync(admin);
        var ana = await factory.RegisterAsync();
        var bruno = await factory.RegisterAsync();
        var anas = await factory.BookAsync(ana, campaign);
        var brunos = await factory.BookAsync(bruno, campaign);

        var seenByAna = await factory.ClientFor(ana.AccessToken).GetFromJsonAsync<JsonElement>($"/api/redemptions?campaignCode={campaign}");
        var seenByStaff = await factory.ClientFor(admin.AccessToken).GetFromJsonAsync<JsonElement>($"/api/redemptions?campaignCode={campaign}");

        Assert.Equal([anas], seenByAna.EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).ToArray());
        Assert.Equivalent(new[] { anas, brunos }, seenByStaff.EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).ToArray());
    }

    [Fact]
    public async Task TheRedemptionOwnerIsTheSignedInUser_NotWhatTheBodySays()
    {
        var admin = await factory.AdminAsync();
        var campaign = await factory.CreateCampaignAsync(admin);
        var ana = await factory.RegisterAsync();

        var response = await factory.ClientFor(ana.AccessToken).PostAsJsonAsync("/api/redemptions",
            new { campaignCode = campaign, reference = "CUS-OWNER-1", uses = 1, ownerId = Guid.NewGuid() });

        var redemption = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ana.UserId, redemption.GetProperty("ownerId").GetGuid());
    }

    [Fact]
    public async Task TheEventLog_IsForStaffOnly()
    {
        var admin = await factory.AdminAsync();
        var campaign = await factory.CreateCampaignAsync(admin);
        var ana = await factory.RegisterAsync();
        var id = await factory.BookAsync(ana, campaign);

        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(ana.AccessToken).GetAsync($"/api/redemptions/{id}/events")).StatusCode);
    }

    [Fact]
    public async Task ACoupon_IsVisibleToItsOwnerAndStaffOnly()
    {
        var admin = await factory.AdminAsync();
        var ana = await factory.RegisterAsync();
        var bruno = await factory.RegisterAsync();
        var redemptionId = Guid.CreateVersion7();
        await using (var db = TestDatabase.Coupons())
        {
            db.Coupons.Add(new Coupon { RedemptionId = redemptionId, OwnerId = ana.UserId, Number = "CP-X", Status = CouponStatus.Issued, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(ana.AccessToken).GetAsync($"/api/coupons/{redemptionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(admin.AccessToken).GetAsync($"/api/coupons/{redemptionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.ClientFor(bruno.AccessToken).GetAsync($"/api/coupons/{redemptionId}")).StatusCode);
    }

    [Fact]
    public async Task Validation_StillAppliesToSignedInUsers()
    {
        var admin = await factory.AdminAsync();
        var campaign = await factory.CreateCampaignAsync(admin);
        var ana = await factory.RegisterAsync();
        var client = factory.ClientFor(ana.AccessToken);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/redemptions", new { campaignCode = campaign, reference = "", uses = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/redemptions", new { campaignCode = campaign, reference = "CUS", uses = RedemptionRules.MaxUsesPerRedemption + 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/redemptions", new { campaignCode = "NOPE", reference = "CUS", uses = 1 })).StatusCode);
    }

    [Fact]
    public async Task EveryResponse_CarriesSecurityHeaders_AndEchoesTheCorrelationId()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/campaigns");
        request.Headers.Add("X-Correlation-Id", "trace-abc-123");

        var response = await factory.NewClient().SendAsync(request);

        Assert.Equal("trace-abc-123", response.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task ProblemResponses_AreProblemJson_WithTheCorrelationId()
    {
        var admin = await factory.AdminAsync();
        var response = await factory.ClientFor(admin.AccessToken).PostAsJsonAsync("/api/campaigns", new { code = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.TryGetProperty("correlationId", out _));
    }
}
