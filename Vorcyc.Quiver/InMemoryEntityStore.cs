namespace Vorcyc.Quiver;

internal sealed class InMemoryEntityStore<TEntity> where TEntity : class
{
    private readonly Dictionary<int, TEntity> _entities = [];

    public int Count => _entities.Count;

    public IEnumerable<TEntity> Values => _entities.Values;

    public IEnumerable<int> Ids => _entities.Keys;

    public void Set(int id, TEntity entity) => _entities[id] = entity;

    public bool TryGetValue(int id, out TEntity? entity) => _entities.TryGetValue(id, out entity);

    public bool Remove(int id) => _entities.Remove(id);

    public void Clear() => _entities.Clear();
}
