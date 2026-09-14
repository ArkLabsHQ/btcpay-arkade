using System.Security.Cryptography;
using System.Text;
using BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

internal sealed class ArkEvmSettlementUpdateBinder(IOptions<MvcNewtonsoftJsonOptions> options) : IModelBinder
{
    private const int MaximumBytes = 32768;
    private const int MaximumDepth = 32;

    /// <inheritdoc />
    public async Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var request = bindingContext.HttpContext.Request;
        var cancellationToken = bindingContext.HttpContext.RequestAborted;
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.HasJsonContentType() || request.ContentLength > MaximumBytes)
        {
            Reject(bindingContext);
            return;
        }

        var buffer = new byte[MaximumBytes + 1];
        try
        {
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken);
                if (read == 0) break;
                length += read;
            }
            if (length > MaximumBytes)
            {
                Reject(bindingContext);
                return;
            }

            try
            {
                var contentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(request.ContentType!);
                var charset = contentType.CharSet?.Trim('"').ToLowerInvariant();
                Encoding encoding = charset switch
                {
                    null or "utf-8" => new UTF8Encoding(false, true),
                    "utf-16" => new UnicodeEncoding(false, true, true),
                    _ => throw new JsonSerializationException()
                };
                using var stream = new MemoryStream(buffer, 0, length, false);
                using var textReader = new StreamReader(stream, encoding, true);
                using var jsonReader = new JsonTextReader(textReader) { MaxDepth = MaximumDepth };
                var serializer = JsonSerializer.Create(options.Value.SerializerSettings);
                serializer.MaxDepth = Math.Min(serializer.MaxDepth ?? MaximumDepth, MaximumDepth);
                serializer.CheckAdditionalContent = true;
                cancellationToken.ThrowIfCancellationRequested();
                var model = serializer.Deserialize<ArkEvmSettlementUpdateData>(jsonReader);
                if (model is null) Reject(bindingContext);
                else bindingContext.Result = ModelBindingResult.Success(model);
            }
            catch (Exception error) when (error is JsonException or DecoderFallbackException or FormatException)
            {
                Reject(bindingContext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static void Reject(ModelBindingContext context)
    {
        context.ModelState.TryAddModelError(context.ModelName, "Invalid settlement settings.");
        context.Result = ModelBindingResult.Failed();
    }
}
