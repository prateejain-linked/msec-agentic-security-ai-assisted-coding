using System.Text.Json;
using CatalogService.Models;
using CatalogService.Simulated;

namespace CatalogService.Services;

/// <summary>
/// Business logic for the product catalog. Talks to the simulated Cosmos
/// container (products), Blob container (images) and Queue (events).
///
/// The test suite documents the expected behaviour.
/// </summary>
public class ProductService
{
    private readonly SimulatedCosmosContainer<Product> _products;
    private readonly SimulatedBlobContainer _images;
    private readonly SimulatedQueue _events;

    public ProductService(
        SimulatedCosmosContainer<Product> products,
        SimulatedBlobContainer images,
        SimulatedQueue events)
    {
        _products = products;
        _images = images;
        _events = events;
        Seed();
    }

    /// <summary>Seeds a deterministic data set used by the pagination tests.</summary>
    private void Seed()
    {
        for (var i = 1; i <= 12; i++)
        {
            var p = new Product
            {
                Id = $"demo-{i}",
                Name = $"Demo Product {i}",
                Category = "demo",
                Price = 10m + i,
                Stock = i
            };
            _products.CreateItemAsync(p);
        }
    }

    public async Task<ServiceResult<IReadOnlyList<Product>>> ListAsync(string? category, int page, int pageSize)
    {
        var all = await _products.QueryAsync(p =>
            category is null || p.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

        var ordered = all.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();

        IReadOnlyList<Product> items = ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return ServiceResult<IReadOnlyList<Product>>.Ok(items);
    }

    public async Task<ServiceResult<Product>> GetAsync(string id)
    {
        var product = await _products.ReadItemAsync(id);

        if (product is null)
        {
            return ServiceResult<Product>.NotFound($"Product '{id}' not found");
        }

        return ServiceResult<Product>.Ok(product);
    }

    public async Task<ServiceResult<Product>> CreateAsync(CreateProductRequest request)
    {
        if (request.Price <= 0)
        {
            return ServiceResult<Product>.BadRequest("Price must be greater than zero.");
        }

        var product = new Product
        {
            Id = Guid.NewGuid().ToString(),
            Name = request.Name,
            Category = request.Category,
            Price = request.Price,
            Stock = request.Stock
        };

        var created = await _products.CreateItemAsync(product);
        return ServiceResult<Product>.Created(created);
    }

    public async Task<ServiceResult<Product>> UpdateAsync(string id, UpdateProductRequest request)
    {
        var existing = await _products.ReadItemAsync(id);
        if (existing is null)
        {
            return ServiceResult<Product>.NotFound($"Product '{id}' not found");
        }

        existing.Name = request.Name;
        existing.Category = request.Category;
        existing.Price = request.Price;
        existing.Stock = request.Stock;

        var updated = await _products.ReplaceItemAsync(existing);
        return ServiceResult<Product>.Ok(updated);
    }

    public async Task<ServiceResult<Product>> DeleteAsync(string id)
    {
        var removed = await _products.DeleteItemAsync(id);
        return removed
            ? ServiceResult<Product>.NoContent()
            : ServiceResult<Product>.NotFound($"Product '{id}' not found");
    }

    /// <summary>
    /// Attaches an image to a product:
    ///   1. validate the product exists (404 otherwise),
    ///   2. upload the bytes to the simulated Blob container,
    ///   3. persist the resulting blob URL on the product (ImageUrl),
    ///   4. publish an "ImageAdded" event onto the simulated Queue.
    /// </summary>
    public async Task<ServiceResult<Product>> AttachImageAsync(string id, byte[] content, string contentType)
    {
        var existing = await _products.ReadItemAsync(id);
        if (existing is null)
        {
            return ServiceResult<Product>.NotFound($"Product '{id}' not found");
        }

        var blobName = Guid.NewGuid().ToString();
        existing.ImageUrl = await _images.UploadAsync(blobName, content);

        var updated = await _products.ReplaceItemAsync(existing);
        var message = JsonSerializer.Serialize(new
        {
            type = "ImageAdded",
            productId = id,
            imageUrl = updated.ImageUrl,
            contentType
        });
        await _events.SendMessageAsync(message);

        return ServiceResult<Product>.Ok(updated);
    }
}
