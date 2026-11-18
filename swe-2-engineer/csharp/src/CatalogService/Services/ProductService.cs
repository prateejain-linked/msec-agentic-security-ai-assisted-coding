using System.Net.Http.Headers;
using System.Text.Json;
using CatalogService.Models;
using CatalogService.Simulated;

namespace CatalogService.Services;

/// <summary>
/// Business logic for the product catalog. Talks to the simulated Cosmos
/// container (products), Blob container (images) and Queue (events).
///
/// NOTE FOR CANDIDATE: several behaviours in this class are incorrect. The test
/// suite documents the expected behaviour. Use your AI agent to locate and fix
/// the defects, then implement the bonus image-upload flow.
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
        if (page <= 0 || pageSize <= 0)
        {
            return ServiceResult<IReadOnlyList<Product>>.BadRequest("Page and pageSize must be greater than zero.");
        }

        var all = await _products.QueryAsync(p =>
            category is null || p.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

        var ordered = all.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();

        // BUG #1: pagination parameters are ignored — every matching item is
        // returned regardless of page / pageSize.
        // FIX: page after filtering and sorting; long arithmetic prevents offset overflow.
        var offset = (long)(page - 1) * pageSize;
        IReadOnlyList<Product> items = offset >= ordered.Count
            ? Array.Empty<Product>()
            : ordered.Skip((int)offset).Take(pageSize).ToList();

        return ServiceResult<IReadOnlyList<Product>>.Ok(items);
    }

    public async Task<ServiceResult<Product>> GetAsync(string id)
    {
        var product = await _products.ReadItemAsync(id);

        // BUG #4: a missing product is returned as a 200 with a null body
        // instead of a 404.
        // FIX: report missing documents, including deleted products, as not found.
        return product is null
            ? ServiceResult<Product>.NotFound($"Product '{id}' not found")
            : ServiceResult<Product>.Ok(product);
    }

    public async Task<ServiceResult<Product>> CreateAsync(CreateProductRequest request)
    {
        // BUG #3: no validation — a zero/negative price (or empty name) is accepted.
        // FIX: reject invalid fields before storing a new product.
        var error = ValidateProduct(request.Name, request.Category, request.Price, request.Stock);
        if (error is not null)
        {
            return ServiceResult<Product>.BadRequest(error);
        }

        var product = new Product
        {
            // BUG #2: the id is never generated, so the stored document and the
            // response come back with an empty id and cannot be fetched later.
            // FIX: unique IDs also prevent the ID-keyed simulator from overwriting prior creates.
            Id = Guid.NewGuid().ToString("N"),
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

        // FIX: validate before mutating the simulator's shared product object.
        var error = ValidateProduct(request.Name, request.Category, request.Price, request.Stock);
        if (error is not null)
        {
            return ServiceResult<Product>.BadRequest(error);
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
    /// BONUS: attach an image to a product. The expected end-to-end behaviour is:
    ///   1. validate the product exists (404 otherwise),
    ///   2. upload the bytes to the simulated Blob container,
    ///   3. persist the resulting blob URL on the product (ImageUrl),
    ///   4. publish an "ImageAdded" event onto the simulated Queue.
    /// This is intentionally NOT implemented — design and build it.
    /// FIX: the implementation below now completes this flow using only local simulators.
    /// </summary>
    public async Task<ServiceResult<Product>> AttachImageAsync(string id, byte[] content, string contentType)
    {
        var product = await _products.ReadItemAsync(id);
        if (product is null)
        {
            return ServiceResult<Product>.NotFound($"Product '{id}' not found");
        }

        if (content.Length == 0)
        {
            return ServiceResult<Product>.BadRequest("Image content must not be empty.");
        }

        if (!MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
            || mediaType.MediaType is not string type
            || !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || type == "image/*")
        {
            return ServiceResult<Product>.BadRequest("Content type must be a specific image media type.");
        }

        // FIX: use a unique blob name so successive uploads do not overwrite stored bytes.
        var blobName = $"{product.Id}/{Guid.NewGuid():N}";
        product.ImageUrl = await _images.UploadAsync(blobName, content);
        var updated = await _products.ReplaceItemAsync(product);

        // Publish only after the product points to the uploaded bytes.
        await _events.SendMessageAsync(JsonSerializer.Serialize(new
        {
            type = "ImageAdded",
            productId = updated.Id,
            imageUrl = updated.ImageUrl
        }));

        return ServiceResult<Product>.Ok(updated);
    }

    private static string? ValidateProduct(string? name, string? category, decimal price, int stock)
    {
        // FIX: keep create and update validation consistent; zero stock remains valid.
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Name must not be empty.";
        }

        if (string.IsNullOrWhiteSpace(category))
        {
            return "Category must not be empty.";
        }

        if (price <= 0)
        {
            return "Price must be greater than zero.";
        }

        return stock < 0 ? "Stock must not be negative." : null;
    }
}
