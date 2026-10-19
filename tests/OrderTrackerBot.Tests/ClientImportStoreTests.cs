using OrderTrackerBot.Api.Client;
using Xunit;

namespace OrderTrackerBot.Tests;

public class ClientImportStoreTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 10, 0, 0, DateTimeKind.Utc);
    private static readonly byte[] File = [1, 2, 3];

    [Fact]
    public void A_kept_upload_is_returned_once_to_its_own_seller()
    {
        var store = new ClientImportStore();
        var id = store.Add(7, "customers", "clients.xlsx", File, Now);

        var waiting = store.Take(id!, 7, Now.AddMinutes(1));

        Assert.NotNull(waiting);
        Assert.Equal(("customers", "clients.xlsx"), (waiting.Kind, waiting.FileName));
        Assert.Null(store.Take(id!, 7, Now.AddMinutes(1)));
    }

    [Fact]
    public void Another_seller_cannot_take_the_upload()
    {
        var store = new ClientImportStore();
        var id = store.Add(7, "catalog", "products.csv", File, Now);

        Assert.Null(store.Take(id!, 8, Now));
        Assert.Null(store.Take(id!, 7, Now));
    }

    [Fact]
    public void An_upload_expires_after_fifteen_minutes()
    {
        var store = new ClientImportStore();
        var id = store.Add(7, "catalog", "products.csv", File, Now);

        Assert.Null(store.Take(id!, 7, Now.Add(ClientImportStore.Lifetime)));
    }

    [Fact]
    public void Too_many_waiting_uploads_are_refused()
    {
        var store = new ClientImportStore();
        for (var i = 0; i < ClientImportStore.MaxWaiting; i++)
            Assert.NotNull(store.Add(7, "catalog", "p.csv", File, Now));

        Assert.Null(store.Add(7, "catalog", "p.csv", File, Now));
        Assert.NotNull(store.Add(7, "catalog", "p.csv", File, Now.Add(ClientImportStore.Lifetime)));
    }
}
