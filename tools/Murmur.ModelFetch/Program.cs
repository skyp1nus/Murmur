using Murmur.ModelFetch;
using Murmur.Models;

return await FetchRunner.RunAsync(args, Console.Out, Console.Error, ModelCatalogSources.Default, CancellationToken.None);
