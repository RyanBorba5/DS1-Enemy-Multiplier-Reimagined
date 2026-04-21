using SoulsFormats;

namespace DS1_Enemy_Multiplier;

public class EntityIdRegistry
{
    private readonly HashSet<int> _entityIds;

    private EntityIdRegistry(HashSet<int> entityIds)
    {
        _entityIds = entityIds;
    }

    public IReadOnlySet<int> AllEntityIds => _entityIds;

    public bool IsKnownEntityId(int value) => _entityIds.Contains(value);

    public static EntityIdRegistry BuildFromMsbs(IEnumerable<string> msbPaths)
    {
        var ids = new HashSet<int>();

        foreach (var path in msbPaths)
        {
            var msb = MSB1.Read(path);

            foreach (var enemy in msb.Parts.Enemies)
                if (enemy.EntityID != -1)
                    ids.Add(enemy.EntityID);

            foreach (var dummy in msb.Parts.DummyEnemies)
                if (dummy.EntityID != -1)
                    ids.Add(dummy.EntityID);
        }

        return new EntityIdRegistry(ids);
    }
}
