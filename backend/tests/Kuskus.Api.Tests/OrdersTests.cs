using System.Net;
using System.Net.Http.Json;
using Kuskus.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using static Kuskus.Api.Controllers.Admin.CategoriesController;
using static Kuskus.Api.Controllers.Admin.DishesController;
using static Kuskus.Api.Controllers.Admin.SupplyDaysController;
using static Kuskus.Api.Controllers.OrdersController;
using static Kuskus.Api.Controllers.PhoneVerificationController;
using static Kuskus.Api.Controllers.PublicController;

namespace Kuskus.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class OrdersTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string ClientPhone = "0501234567";

    private readonly ApiFactory _factory = new(postgres);
    private HttpClient _admin = null!;
    private HttpClient _client = null!;

    private int _chicken;
    private int _thigh;
    private int _meat;
    private string? _token;

    public async Task InitializeAsync()
    {
        _admin = await _factory.CreateAdminClientAsync();
        _client = _factory.CreateApiClient();

        // Every weekday is a supply day that closes at midnight the day before, so the
        // first open date is always one to two days away whatever day the test runs.
        var days = Enum.GetValues<DayOfWeek>().Select(d =>
            new SupplyDayDto(d, true, (DayOfWeek)(((int)d + 6) % 7), new TimeOnly(0, 0)));
        (await _admin.PutAsJsonAsync("/api/admin/supply-days", days, TestFiles.Json)).EnsureSuccessStatusCode();

        var category = await (await _admin.PostAsJsonAsync("/api/admin/categories", new { name = "עופות" })).Read<CategoryDto>();
        _chicken = (await CreateDish(new DishInput(
            "עוף בתנור", category.Id, "טעים", null, SellBy.Units, ChoiceMode.Fixed, null, null, null, null, false, false,
            [new OptionInput(null, "חצי", 1, 40, false), new OptionInput(null, "שלם", 1, 70, true)], []))).Id;
        _thigh = (await CreateDish(new DishInput(
            "ירך", category.Id, null, null, SellBy.Units, ChoiceMode.Fixed, null, null, null, null, true, false,
            [new OptionInput(null, "יחידה", 1, 12, true)], [_chicken]))).Id;
        _meat = (await CreateDish(new DishInput(
            "בשר טחון", category.Id, null, null, SellBy.Weight, ChoiceMode.Free, 0.5m, 3m, 0.25m, 90m, false, false, null, []))).Id;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<DishDto> CreateDish(DishInput input) =>
        await (await _admin.PostAsJsonAsync("/api/admin/dishes", input, TestFiles.Json)).Read<DishDto>();

    private async Task<MenuDto> Menu() => await (await _client.GetAsync("/api/menu")).Read<MenuDto>();

    private async Task<string> Verify(string phone = ClientPhone)
    {
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsJsonAsync("/api/phone-verification/send", new { phone })).StatusCode);
        var confirmed = await (await _client.PostAsJsonAsync(
            "/api/phone-verification/confirm", new { phone, code = _factory.WhatsApp.LastCode(ClientPhone) })).Read<ConfirmedDto>();
        return confirmed.Token;
    }

    private async Task<object> ValidOrder(Action<Dictionary<string, object?>>? change = null)
    {
        var order = new Dictionary<string, object?>
        {
            ["phone"] = "050-123-4567",
            ["name"] = " דנה ",
            ["address"] = "הרצל 1, חיפה",
            ["supplyDate"] = (await Menu()).SupplyDates[0].Date.ToString("yyyy-MM-dd"),
            ["fulfillmentMethod"] = "Delivery",
            ["paymentMethod"] = "OnDelivery",
            ["notes"] = "בלי חריף",
            // One confirmed phone per test: codes are limited to three per phone.
            ["verificationToken"] = _token ??= await Verify(),
            ["items"] = new object[]
            {
                new { dishId = _chicken, optionId = (int?)null, quantity = 2m, addOns = new[] { new { dishId = _thigh, optionId = (int?)null, quantity = 3m } } },
                new { dishId = _meat, optionId = (int?)null, quantity = 1.5m, addOns = Array.Empty<object>() },
            },
        };
        change?.Invoke(order);
        return order;
    }

    private Task<HttpResponseMessage> Place(object order) => _client.PostAsJsonAsync("/api/orders", order, TestFiles.Json);

    // ---------- Menu ----------

    [Fact]
    public async Task Menu_lists_categories_dishes_and_open_supply_dates()
    {
        var menu = await Menu();

        Assert.Equal(["עופות"], menu.Categories.Select(c => c.Name));
        Assert.Equal(3, menu.Dishes.Count);
        Assert.Equal([_thigh], menu.Dishes.Single(d => d.Id == _chicken).AddOnDishIds);
        Assert.True(menu.Dishes.Single(d => d.Id == _thigh).IsAddOnOnly);
        Assert.Equal(6, menu.SupplyDates.Count);
        Assert.True(menu.SupplyDates[0].Date > DateOnly.FromDateTime(DateTime.UtcNow));
    }

    [Fact]
    public async Task Removed_dishes_and_closed_dates_leave_the_menu()
    {
        var first = (await Menu()).SupplyDates[0].Date;
        (await _admin.PostAsJsonAsync("/api/admin/closed-dates", new { date = first })).EnsureSuccessStatusCode();
        (await _admin.DeleteAsync($"/api/admin/dishes/{_meat}")).EnsureSuccessStatusCode();

        var menu = await Menu();
        Assert.DoesNotContain(menu.SupplyDates, d => d.Date == first);
        Assert.DoesNotContain(menu.Dishes, d => d.Id == _meat);
    }

    // ---------- Phone verification ----------

    [Fact]
    public async Task Login_code_is_sent_by_whatsapp_and_confirmed_once()
    {
        await _client.PostAsJsonAsync("/api/phone-verification/send", new { phone = "050-123-4567" });
        var code = _factory.WhatsApp.LastCode(ClientPhone);

        var confirm = new { phone = ClientPhone, code };
        Assert.False(string.IsNullOrEmpty((await (await _client.PostAsJsonAsync("/api/phone-verification/confirm", confirm)).Read<ConfirmedDto>()).Token));
        // The code is used up.
        await (await _client.PostAsJsonAsync("/api/phone-verification/confirm", confirm)).AssertInvalid("Code", "codeExpired");
    }

    [Fact]
    public async Task Wrong_code_is_rejected_and_guessing_is_limited()
    {
        await _client.PostAsJsonAsync("/api/phone-verification/send", new { phone = ClientPhone });
        var right = _factory.WhatsApp.LastCode(ClientPhone);
        var wrong = right == "000000" ? "000001" : "000000";

        for (var i = 0; i < 5; i++)
            await (await _client.PostAsJsonAsync("/api/phone-verification/confirm", new { phone = ClientPhone, code = wrong }))
                .AssertInvalid("Code", "codeWrong");

        // After five wrong guesses even the right code no longer works.
        await (await _client.PostAsJsonAsync("/api/phone-verification/confirm", new { phone = ClientPhone, code = right }))
            .AssertInvalid("Code", "codeExpired");
    }

    [Fact]
    public async Task Codes_are_limited_per_phone_and_stored_hashed()
    {
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsJsonAsync("/api/phone-verification/send", new { phone = ClientPhone })).StatusCode);
        var fourth = await _client.PostAsJsonAsync("/api/phone-verification/send", new { phone = ClientPhone });
        Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode);
        Assert.Equal(3, _factory.WhatsApp.MessagesTo(ClientPhone).Count());

        await using var db = _factory.CreateDbContext();
        Assert.All(await db.LoginCodes.ToListAsync(), c => Assert.DoesNotContain(_factory.WhatsApp.LastCode(ClientPhone), c.CodeHash));
    }

    [Fact]
    public async Task Invalid_phone_gets_no_code()
    {
        await (await _client.PostAsJsonAsync("/api/phone-verification/send", new { phone = "12" })).AssertInvalid("Phone", "phone");
        Assert.Empty(_factory.WhatsApp.Sent);
    }

    // ---------- Placing orders ----------

    [Fact]
    public async Task Guest_order_is_saved_with_prices_from_the_menu()
    {
        var confirmation = await (await Place(await ValidOrder())).Read<ConfirmationDto>();

        // 2 × 70 + 3 × 12 + 1.5 kg × 90
        Assert.Equal(311m, confirmation.Total);
        Assert.Equal(3, confirmation.Items.Count);
        Assert.Equal([false, true, false], confirmation.Items.Select(i => i.IsAddOn));

        await using var db = _factory.CreateDbContext();
        var order = await db.Orders.Include(o => o.Items).SingleAsync();
        Assert.Equal((ClientPhone, "דנה", OrderStatus.New, false), (order.Phone, order.Name, order.Status, order.IsPaid));
        Assert.Null(order.UserId);
        Assert.Equal(311m, order.Total);
        Assert.Equal(order.Items.Single(i => i.DishId == _chicken).Id, order.Items.Single(i => i.DishId == _thigh).ParentItemId);
        Assert.Equal("שלם", order.Items.Single(i => i.DishId == _chicken).OptionLabel);
    }

    [Fact]
    public async Task Later_price_changes_do_not_touch_a_saved_order()
    {
        await (await Place(await ValidOrder())).Read<ConfirmationDto>();

        var dish = await (await _admin.GetAsync($"/api/admin/dishes/{_meat}")).Read<DishDto>();
        var input = new DishInput(dish.Name, dish.CategoryId, null, null, dish.SellBy, dish.ChoiceMode, 0.5m, 3m, 0.25m, 200m, false, false, null, []);
        (await _admin.PutAsJsonAsync($"/api/admin/dishes/{_meat}", input, TestFiles.Json)).EnsureSuccessStatusCode();

        await using var db = _factory.CreateDbContext();
        Assert.Equal(90m, (await db.OrderItems.SingleAsync(i => i.DishId == _meat)).UnitPrice);
    }

    [Fact]
    public async Task Client_and_every_admin_phone_get_a_whatsapp_message()
    {
        (await _admin.PostAsJsonAsync("/api/admin/notify-phones", new { phone = "0521111111" })).EnsureSuccessStatusCode();
        (await _admin.PostAsJsonAsync("/api/admin/notify-phones", new { phone = "0522222222" })).EnsureSuccessStatusCode();

        var confirmation = await (await Place(await ValidOrder())).Read<ConfirmationDto>();

        Assert.Contains(_factory.WhatsApp.MessagesTo(ClientPhone), m => m.Contains("ההזמנה שלך התקבלה") && m.Contains("₪311"));
        foreach (var phone in new[] { "0521111111", "0522222222" })
            Assert.Contains(_factory.WhatsApp.MessagesTo(phone), m => m.Contains($"הזמנה חדשה #{confirmation.Id}") && m.Contains("₪311"));
    }

    [Fact]
    public async Task Order_is_saved_even_when_whatsapp_fails()
    {
        (await _admin.PostAsJsonAsync("/api/admin/notify-phones", new { phone = "0521111111" })).EnsureSuccessStatusCode();
        var order = await ValidOrder();
        _factory.WhatsApp.FailFor.Add("0521111111");
        _factory.WhatsApp.FailFor.Add(ClientPhone);

        Assert.Equal(HttpStatusCode.OK, (await Place(order)).StatusCode);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task Transfer_payment_shows_the_payment_phone()
    {
        (await _admin.PutAsJsonAsync("/api/admin/settings", new
        {
            deliveryEnabled = true, pickupEnabled = true, deliveryAreaText = (string?)null, deliveryFeeText = (string?)null,
            kashrutText = (string?)null, paymentPhone = "052-9999999",
        })).EnsureSuccessStatusCode();

        var confirmation = await (await Place(await ValidOrder(o => o["paymentMethod"] = "Transfer"))).Read<ConfirmationDto>();

        Assert.Equal("052-9999999", confirmation.PaymentPhone);
        Assert.Contains(_factory.WhatsApp.MessagesTo(ClientPhone), m => m.Contains("052-9999999"));
    }

    [Fact]
    public async Task Transfer_needs_a_payment_phone_in_settings()
    {
        var response = await Place(await ValidOrder(o => o["paymentMethod"] = "Transfer"));
        await response.AssertInvalid("PaymentMethod", "paymentUnavailable");
    }

    [Fact]
    public async Task Pickup_does_not_need_an_address_but_delivery_does()
    {
        Assert.Equal(HttpStatusCode.OK, (await Place(await ValidOrder(o => { o["fulfillmentMethod"] = "Pickup"; o["address"] = null; }))).StatusCode);
        await (await Place(await ValidOrder(o => o["address"] = " "))).AssertInvalid("Address", "required");
    }

    [Fact]
    public async Task Disabled_fulfillment_is_rejected()
    {
        (await _admin.PutAsJsonAsync("/api/admin/settings", new
        {
            deliveryEnabled = false, pickupEnabled = true, deliveryAreaText = (string?)null, deliveryFeeText = (string?)null,
            kashrutText = (string?)null, paymentPhone = (string?)null,
        })).EnsureSuccessStatusCode();

        await (await Place(await ValidOrder())).AssertInvalid("FulfillmentMethod", "fulfillmentUnavailable");
    }

    [Fact]
    public async Task Phone_must_be_verified_for_the_order_phone()
    {
        await (await Place(await ValidOrder(o => o["verificationToken"] = null))).AssertInvalid("Phone", "phoneNotVerified");
        await (await Place(await ValidOrder(o => o["verificationToken"] = "garbage"))).AssertInvalid("Phone", "phoneNotVerified");
        // A token for one phone does not work for another.
        await (await Place(await ValidOrder(o => o["phone"] = "0529999999"))).AssertInvalid("Phone", "phoneNotVerified");
    }

    [Fact]
    public async Task Supply_date_must_be_open()
    {
        var closed = (await Menu()).SupplyDates[0].Date;
        (await _admin.PostAsJsonAsync("/api/admin/closed-dates", new { date = closed })).EnsureSuccessStatusCode();
        await (await Place(await ValidOrder(o => o["supplyDate"] = closed.ToString("yyyy-MM-dd")))).AssertInvalid("SupplyDate", "supplyDateUnavailable");

        var past = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1).ToString("yyyy-MM-dd");
        await (await Place(await ValidOrder(o => o["supplyDate"] = past))).AssertInvalid("SupplyDate", "supplyDateUnavailable");
    }

    [Fact]
    public async Task Order_needs_name_and_items_and_valid_dishes()
    {
        await (await Place(await ValidOrder(o => o["name"] = ""))).AssertInvalid("Name", "required");
        await (await Place(await ValidOrder(o => o["items"] = Array.Empty<object>()))).AssertInvalid("Items", "emptyOrder");

        // The add-on-only dish cannot be ordered on its own, and a sold-out dish cannot be ordered at all.
        await (await Place(await ValidOrder(o => o["items"] = new[] { new { dishId = _thigh, optionId = (int?)null, quantity = 1m, addOns = Array.Empty<object>() } })))
            .AssertInvalid("items[0]", "dishUnavailable");
        (await _admin.PutAsJsonAsync($"/api/admin/dishes/{_meat}/sold-out", new { isSoldOut = true })).EnsureSuccessStatusCode();
        await (await Place(await ValidOrder())).AssertInvalid("items[1]", "dishUnavailable");
    }

    [Fact]
    public async Task Order_endpoints_are_open_to_guests_but_need_the_request_header()
    {
        var bare = _factory.CreateClient(new() { HandleCookies = false });
        var response = await bare.PostAsJsonAsync("/api/orders", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bare.GetAsync("/api/menu")).StatusCode);
    }
}

[Collection(PostgresCollection.Name)]
public sealed class ShowCodeOnScreenTests(PostgresFixture postgres)
{
    private static async Task<HttpResponseMessage> Send(ApiFactory factory) =>
        await factory.CreateApiClient().PostAsJsonAsync("/api/phone-verification/send", new { phone = "0501234567" });

    [Fact]
    public async Task Code_is_not_returned_by_default()
    {
        using var factory = new ApiFactory(postgres);
        var response = await Send(factory);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Code_is_returned_when_the_test_setting_is_on()
    {
        using var factory = new ApiFactory(postgres, new() { ["WhatsApp:ShowCodeOnScreen"] = "true" });
        var reply = await (await Send(factory)).Read<TestCodeDto>();
        Assert.Equal(factory.WhatsApp.LastCode("0501234567"), reply.DevCode);
    }
}
