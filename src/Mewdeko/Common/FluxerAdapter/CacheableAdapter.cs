namespace Mewdeko.Common.FluxerAdapter;

/// <summary>
///     Cacheable members Mewdeko relies on that Fluxer.Net does not have.
/// </summary>
public static class CacheableAdapter
{
    extension<TEntity>(Cacheable<TEntity> cacheable) where TEntity : ISnowflake
    {
        /// <summary>
        ///     Whether the entity was in the cache when the event fired.
        /// </summary>
        public bool HasValue => cacheable.Value is not null;
    }
}
