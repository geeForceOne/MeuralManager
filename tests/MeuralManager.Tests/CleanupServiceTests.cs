using MeuralManager.Core.Models;
using MeuralManager.Core.Services;

namespace MeuralManager.Tests;

public class CleanupServiceTests
{
    private static MeuralItem Item(long id, string createdAt = "2026-01-01") => new() { Id = id, CreatedAt = createdAt };
    private static MeuralGallery Gallery(long id, params long[] itemIds) => new() { Id = id, ItemIds = [.. itemIds] };

    [Fact]
    public void OrphanItems_AreThoseInNoPlaylist_OldestFirst()
    {
        var items = new[] { Item(1, "2026-03-01"), Item(2), Item(3, "2026-02-01"), Item(4) };
        var galleries = new[] { Gallery(10, 2), Gallery(11, 4, 99) };

        var orphans = CleanupService.FindOrphanItems(items, galleries);

        Assert.Equal([3L, 1L], orphans.Select(i => i.Id!.Value));
    }

    [Fact]
    public void OrphanItems_PlaylistWithoutItemList_ProtectsNothing()
    {
        var orphans = CleanupService.FindOrphanItems([Item(1)], [new MeuralGallery { Id = 10 }]);

        Assert.Single(orphans);
    }

    [Fact]
    public void OrphanItems_ItemWithoutId_IsNeverAnOrphan()
    {
        var orphans = CleanupService.FindOrphanItems([new MeuralItem { Name = "no id" }], []);

        Assert.Empty(orphans);
    }

    [Fact]
    public void UnusedGalleries_AreThoseOnNoFrame()
    {
        var galleries = new[] { Gallery(10), Gallery(11), new MeuralGallery { Name = "no id" } };

        var unused = CleanupService.FindUnusedGalleries(galleries, [11]);

        Assert.Equal([10L], unused.Select(g => g.Id!.Value));
    }
}
