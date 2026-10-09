extern alias TigerCsWeb;

using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Display = TigerCsWeb::TigerCS.Web.Pages.Collections.CollectionsDisplay;

namespace TigerCS.Tests.Web;

/// <summary>Receivables page: the shell never waits for data; results arrive as a fragment; filters, columns and states.</summary>
public sealed class PactReceivablesRenderTests
{
    [Fact]
    public async Task InitialPage_RendersFiltersAndALoadingResultsArea_WithoutWaitingForTheInstalmentData()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var response = await client.GetAsync("/Collections/Receivables?towerId=7");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Empty(api.Calls("/receivables/instalments"));                        // the shell made no data call
        Assert.Contains("data-results-loading", html);                              // loading is shown in the results area only
        Assert.Contains("/js/receivables.js", html);
        // Filters: tower picker, Month + Year, From + To, Last 6 months, Payment status, Minimum outstanding amount (default 100).
        Assert.Contains("<option value=\"7\" selected=\"selected\">124 - Tower 124</option>", html);
        Assert.Contains("name=\"month\"", html); Assert.Contains("name=\"year\"", html);
        Assert.Contains("name=\"dateFrom\"", html); Assert.Contains("name=\"dateTo\"", html);
        Assert.Contains("data-last-six-months", html);
        Assert.Contains("name=\"paymentStatus\"", html);
        Assert.Contains("Outstanding (unpaid", html);
        Assert.Contains("Minimum outstanding amount (AED)", html);
        Assert.Contains("name=\"minAmount\"", html); Assert.Contains("value=\"100\"", html);
        Assert.DoesNotContain("name=\"companyId\"", html);
    }

    [Fact]
    public async Task ResultsFragment_ShowsInstalmentColumns_WithPaymentStatusSeparateFromDueOverdue_AndSendsTheFilters()
    {
        var api = new FakeCollectionsApi { Breakdown = true };
        api.Rows.Add(FakeCollectionsApi.Row("PartiallyPaid", "Overdue", 600m, 1000m, 400m, "INV-7"));
        api.Rows.Add(FakeCollectionsApi.Row("Unpaid", "Due", 250m, 250m, 0m, "INV-8"));
        api.Rows.Add(FakeCollectionsApi.Row("Unknown", "Overdue", 90m, null, null, "INV-9"));
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var response = await client.GetAsync("/Collections/Receivables?handler=Results&view=instalments&towerId=7&dateFrom=2026-01-01&dateTo=2026-10-31&paymentStatus=outstanding&minAmount=100&search=3001");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("web;dur=", response.Headers.GetValues("Server-Timing").Single());
        Assert.Contains("sql;dur=12", response.Headers.GetValues("Server-Timing").Single());
        Assert.True(response.Headers.CacheControl!.NoStore);
        var call = Assert.Single(api.Calls("/receivables/instalments"));
        foreach (var expected in new[] { "towerId=7", "dateFrom=2026-01-01", "dateTo=2026-10-31", "paymentStatus=outstanding", "minAmount=100", "search=3001" }) Assert.Contains(expected, call);
        Assert.DoesNotContain("<html", html);                                        // a fragment, not a page
        foreach (var column in new[] { ">Tower<", ">Unit<", ">Customer<", ">Voucher<", ">Due date<", "Original instalment", ">Paid<", ">Remaining<", ">Payment status<", "Due / Overdue" }) Assert.Contains(column, html);
        Assert.Contains("data-payment-status-label=\"PartiallyPaid\"", html);
        Assert.Contains("data-classification=\"Overdue\"", html);                    // partially paid AND overdue in the same row
        Assert.Contains("Partially paid", html);
        Assert.Contains("1,000.00", html); Assert.Contains("400.00", html); Assert.Contains("600.00", html);
        Assert.Contains("Needs verification", html);                                 // unknown status is never guessed
        Assert.Contains("Not provided by the source", html);                         // unknown original / paid amounts are not invented
        Assert.Contains("data-payment-views", html);
    }

    [Theory]
    [InlineData("paid")]
    [InlineData("all")]
    public async Task FullyPaidAndAll_DoNotSendTheMinimum_AndTheFieldIsDisabledWithAnExplanation(string status)
    {
        var api = new FakeCollectionsApi();
        api.Rows.Add(FakeCollectionsApi.Row("FullyPaid", "NotApplicable", 0m, 500m, 500m));
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var shell = await (await client.GetAsync($"/Collections/Receivables?paymentStatus={status}&minAmount=1000")).Content.ReadAsStringAsync();
        Assert.Matches("<input[^>]*name=\"minAmount\"[^>]*disabled", shell);
        Assert.Contains("Not applied to Fully paid and All, so paid instalments are never hidden.", shell);
        await client.GetAsync($"/Collections/Receivables?handler=Results&paymentStatus={status}&minAmount=1000");
        var call = Assert.Single(api.Calls("/receivables/instalments"));
        Assert.Contains($"paymentStatus={status}", call);
        Assert.DoesNotContain("minAmount", call);                                     // the API/SQL ignore it too: a paid row can never be hidden by it
    }

    [Theory]
    [InlineData(2026, 2, "2026-02-01", "2026-02-28")]
    [InlineData(2028, 2, "2028-02-01", "2028-02-29")]       // leap February
    [InlineData(2026, 12, "2026-12-01", "2026-12-31")]
    [InlineData(2027, 1, "2027-01-01", "2027-01-31")]       // year change
    [InlineData(2026, 4, "2026-04-01", "2026-04-30")]
    public async Task MonthAndYear_SetFromAndToToTheFirstAndLastDay(int year, int month, string from, string to)
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var shell = await (await client.GetAsync($"/Collections/Receivables?month={month}&year={year}")).Content.ReadAsStringAsync();
        Assert.Contains($"value=\"{from}\"", shell); Assert.Contains($"value=\"{to}\"", shell);
        Assert.Matches($"<option value=\"{month}\" selected", shell); Assert.Matches($"<option value=\"{year}\" selected", shell);
        await client.GetAsync($"/Collections/Receivables?handler=Results&month={month}&year={year}");
        var call = Assert.Single(api.Calls("/receivables/instalments"));
        Assert.Contains($"dateFrom={from}", call); Assert.Contains($"dateTo={to}", call);
    }

    [Fact]
    public async Task ACustomRange_KeepsTheMonthSelectorsOnCustom_AndLastSixMonthsIsStillOffered()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Receivables?dateFrom=2025-09-15&dateTo=2026-03-15")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("<option value=\"9\" selected", html);
        Assert.Contains("value=\"2025-09-15\"", html); Assert.Contains("value=\"2026-03-15\"", html);
        Assert.Contains("data-last-six-months", html);
        var expectedFrom = Display.DubaiToday().AddMonths(-6).ToString("yyyy-MM-dd");
        Assert.Contains($"dateFrom={expectedFrom}", html);
    }

    [Fact]
    public async Task ByUnit_IsTheDefaultView_OneRowPerUnit_WithItsInstalmentsExpandable_AndTheViewSurvivesFiltersAndPaging()
    {
        var api = new FakeCollectionsApi();
        api.Rows.Add(FakeCollectionsApi.Row("Unpaid", "Overdue", 300m, 300m, 0m, "INV-1"));
        api.Rows.Add(FakeCollectionsApi.Row("Unpaid", "Due", 200m, 200m, 0m, "INV-2"));
        api.Rows.Add(FakeCollectionsApi.Row("Unpaid", "Overdue", 150m, 150m, 0m, "INV-3", "TP124-2002"));
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();

        var shell = await (await client.GetAsync("/Collections/Receivables")).Content.ReadAsStringAsync();
        Assert.Contains("name=\"view\" value=\"units\" checked", shell);                        // By unit is the default ...
        Assert.Contains("name=\"view\" value=\"instalments\"", shell);                         // ... By instalment stays available

        var html = await (await client.GetAsync("/Collections/Receivables?handler=Results&search=Example")).Content.ReadAsStringAsync();
        Assert.Contains("view=units", Assert.Single(api.Calls("/receivables/instalments")));
        foreach (var column in new[] { ">Tower<", ">Unit<", ">Customer<", ">Total remaining<", ">Overdue<", ">Due<", ">Not yet due<" }) Assert.Contains(column, html);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "data-unit-row").Count);        // two units, not three instalments
        Assert.Contains("data-unit-toggle", html);                                                          // expandable ...
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "data-unit-detail").Count);      // ... with its instalments inside the unit
        Assert.Contains("INV-1", html); Assert.Contains("INV-2", html); Assert.Contains("INV-3", html);
        Assert.Contains("500.00", html);                                                                    // 300 + 200: the unit's total remaining
        Assert.Contains("2 units", html); Assert.Contains("3 instalments", html);
        Assert.Contains("data-unit-month=\"2026-09\"", html);                                              // the months with unpaid instalments sit in the unit row ...
        Assert.Contains(">Overdue</span>", html);                                                           // ... each with its Due / Overdue label

        // By instalment is the unchanged one-row-per-instalment table, and the chosen view is part of every link.
        var flat = await (await client.GetAsync("/Collections/Receivables?handler=Results&view=instalments")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("data-unit-row", flat);
        Assert.Contains("3 instalments", flat);
        var full = await (await client.GetAsync("/Collections/Receivables?render=full&view=instalments")).Content.ReadAsStringAsync();
        Assert.Contains("name=\"view\" value=\"instalments\" checked", full);
    }

    [Fact]
    public async Task RenderFull_IsTheNoJavaScriptFallback_AndIncludesTheResults()
    {
        var api = new FakeCollectionsApi();
        api.Rows.Add(FakeCollectionsApi.Row("Unpaid", "Overdue", 300m, 300m, 0m));
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Receivables?render=full")).Content.ReadAsStringAsync();
        Assert.Contains("Example Customer", html);
        Assert.DoesNotContain("data-results-loading", html);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "does not have permission")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "not an empty result")]
    public async Task Errors_AreExplicitInTheResultsArea_NeverAnEmptyList(HttpStatusCode status, string text)
    {
        var api = new FakeCollectionsApi { Status = status };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Receivables?handler=Results")).Content.ReadAsStringAsync();
        Assert.Contains(text, html);
        Assert.DoesNotContain("receivables-table", html);
        Assert.DoesNotContain("No instalments match", html);
    }

    [Fact]
    public async Task BeforeTheFirstSnapshot_ThePageRendersWithItsFiltersAndAWorkingLoadDataAction()
    {
        var api = new FakeCollectionsApi { NothingLoaded = true, PaidRetained = false };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var shell = await client.GetAsync("/Collections/Receivables");
        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        Assert.Contains("name=\"paymentStatus\"", await shell.Content.ReadAsStringAsync());
        var html = await (await client.GetAsync("/Collections/Receivables?handler=Results&dateFrom=2026-01-01&dateTo=2026-10-31")).Content.ReadAsStringAsync();
        Assert.Contains("No receivables data has been loaded yet.", html);
        Assert.Contains("data-load-button", html); Assert.Contains(">Load data<", html);
        Assert.Contains("data-nothing-loaded=\"true\"", html);
        Assert.Contains("data-paid=\"false\"", html);                                   // Fully paid / All stay disabled against a dataset that holds no paid rows
        Assert.DoesNotContain("No instalments match", html);
        Assert.Contains("not \"no instalments\"", html);
    }

    [Fact]
    public async Task PaymentViewAvailability_FollowsTheLoadedData()
    {
        var api = new FakeCollectionsApi { Breakdown = false, PaidRetained = true };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Receivables?handler=Results")).Content.ReadAsStringAsync();
        Assert.Contains("data-breakdown=\"false\"", html);                               // Unpaid / Partially paid cannot be told apart by the deployed procedures
        Assert.Contains("data-paid=\"true\"", html);
    }

    [Fact]
    public async Task UnitRows_ShowMonthlyAmountsWithBusinessLabels_ThatAddUpToTheUnitTotal_AndMatchTheExpansion()
    {
        var api = new FakeCollectionsApi();
        api.Rows.Add(FakeCollectionsApi.Row("Unpaid", "Overdue", 300m, 300m, 0m, "INV-1") with { DueDate = new DateOnly(2026, 8, 5) });
        api.Rows.Add(FakeCollectionsApi.Row("Unpaid", "Overdue", 120m, 120m, 0m, "INV-2") with { DueDate = new DateOnly(2026, 9, 5) });
        api.Rows.Add(FakeCollectionsApi.Row("Unpaid", "Overdue", 80m, 80m, 0m, "INV-3") with { DueDate = new DateOnly(2026, 9, 20) });
        api.Rows.Add(FakeCollectionsApi.Row("Unpaid", "Due", 200m, 200m, 0m, "INV-4") with { DueDate = new DateOnly(2026, 10, 5) });
        api.Rows.Add(FakeCollectionsApi.Row("Unpaid", "NotYetDue", 150m, 150m, 0m, "INV-5") with { DueDate = new DateOnly(2026, 11, 5) });
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Receivables?handler=Results")).Content.ReadAsStringAsync();

        var row = System.Text.RegularExpressions.Regex.Match(html, "<tr data-unit-row>.*?</tr>", System.Text.RegularExpressions.RegexOptions.Singleline).Value;
        var months = System.Text.RegularExpressions.Regex.Matches(row, "data-unit-month=\"(\\d{4}-\\d{2})\" data-month-remaining=\"([\\d.]+)\" data-classification=\"(\\w+)\"")
            .Select(m => (Month: m.Groups[1].Value, Amount: decimal.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), Label: m.Groups[3].Value)).ToList();
        Assert.Equal([("2026-08", 300m, "Overdue"), ("2026-09", 200m, "Overdue"), ("2026-10", 200m, "Due"), ("2026-11", 150m, "NotYetDue")], months);
        Assert.Equal(850m, months.Sum(m => m.Amount));                                       // the months add up to the unit's total remaining ...
        Assert.Contains("850.00", row);                                                      // ... which the row shows
        var detail = System.Text.RegularExpressions.Regex.Match(html, "<tr class=\"unit-detail\".*?</tr>\\s*</tbody>", System.Text.RegularExpressions.RegexOptions.Singleline).Value;
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(detail, "data-payment-status-label").Count);   // the expansion lists every instalment of the unit
        Assert.DoesNotContain("month-card", html);                                           // no month cards: the months live in the unit row
        string ClassTotal(string cls) => System.Text.RegularExpressions.Regex.Match(row, "data-unit-class=\"" + cls + "\">.*?data-class-total>([\\d,.]+)<", System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
        Assert.Equal("500.00", ClassTotal("Overdue")); Assert.Equal("200.00", ClassTotal("Due")); Assert.Equal("150.00", ClassTotal("NotYetDue"));
        Assert.Contains("Aug 2026", row); Assert.Contains("Sep 2026", row);                  // months include the year
    }

    [Fact]
    public async Task DiagnosticsAreCollapsed_ButStillThere()
    {
        var api = new FakeCollectionsApi();
        api.Rows.Add(FakeCollectionsApi.Row("Unpaid", "Due", 200m, 200m, 0m, "INV-4") with { DueDate = new DateOnly(2026, 10, 5) });
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Receivables?handler=Results")).Content.ReadAsStringAsync();
        Assert.Contains("<details class=\"snapshot-status__details\"", html);                // loading diagnostics are collapsed ...
        Assert.Contains("snapshot-status__table", html);                                     // ... but still there
    }
}
