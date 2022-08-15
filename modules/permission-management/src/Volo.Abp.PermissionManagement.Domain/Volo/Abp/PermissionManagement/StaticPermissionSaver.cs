using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Caching;
using Volo.Abp.DependencyInjection;
using Volo.Abp.DistributedLocking;
using Volo.Abp.Uow;

namespace Volo.Abp.PermissionManagement;

public class StaticPermissionSaver : IStaticPermissionSaver, ITransientDependency
{
    protected IStaticPermissionDefinitionStore StaticStore { get; }
    protected IPermissionGroupDefinitionRecordRepository PermissionGroupRepository { get; }
    protected IPermissionDefinitionRecordRepository PermissionRepository { get; }
    protected IPermissionDefinitionSerializer PermissionSerializer { get; }
    protected IDistributedCache Cache { get; }
    protected IApplicationNameAccessor ApplicationNameAccessor { get; }
    public IAbpDistributedLock DistributedLock { get; }
    public PermissionManagementOptions PermissionManagementOptions { get; }
    protected AbpDistributedCacheOptions CacheOptions { get; }
    
    public StaticPermissionSaver(
        IStaticPermissionDefinitionStore staticStore,
        IPermissionGroupDefinitionRecordRepository permissionGroupRepository,
        IPermissionDefinitionRecordRepository permissionRepository,
        IPermissionDefinitionSerializer permissionSerializer,
        IDistributedCache cache, 
        IOptions<AbpDistributedCacheOptions> cacheOptions,
        IApplicationNameAccessor applicationNameAccessor,
        IAbpDistributedLock distributedLock,
        IOptions<PermissionManagementOptions> permissionManagementOptions)
    {
        StaticStore = staticStore;
        PermissionGroupRepository = permissionGroupRepository;
        PermissionRepository = permissionRepository;
        PermissionSerializer = permissionSerializer;
        Cache = cache;
        ApplicationNameAccessor = applicationNameAccessor;
        DistributedLock = distributedLock;
        PermissionManagementOptions = permissionManagementOptions.Value;
        CacheOptions = cacheOptions.Value;
    }
    
    [UnitOfWork]
    public virtual async Task SaveAsync()
    {
        await using var handle = await DistributedLock.TryAcquireAsync(GetDistributedLockKey());

        if (handle == null)
        {
            /* Another instance already did it */
            return;
        }

        var cacheKey = GetCacheKey();
        var cachedHash = await Cache.GetStringAsync(cacheKey);

        var (permissionGroupRecords, permissionRecords) = await PermissionSerializer.SerializeAsync(
            await StaticStore.GetGroupsAsync()
        );

        var currentHash = CalculateHash(
            permissionGroupRecords,
            permissionRecords,
            PermissionManagementOptions.DeletedPermissionGroups,
            PermissionManagementOptions.DeletedPermissions
        );
        
        if (cachedHash == currentHash)
        {
            return;
        }

        await UpdateChangedGroupsAsync(permissionGroupRecords);

        await Cache.SetStringAsync(
            cacheKey,
            currentHash,
            new DistributedCacheEntryOptions {
                SlidingExpiration = TimeSpan.FromDays(2)
            }
        );
    }

    private async Task UpdateChangedGroupsAsync(PermissionGroupDefinitionRecord[] permissionGroupRecords)
    {
        var newRecords = new List<PermissionGroupDefinitionRecord>();
        var changedRecords = new List<PermissionGroupDefinitionRecord>();

        var permissionGroupRecordsInDatabase = (await PermissionGroupRepository.GetListAsync())
            .ToDictionary(x => x.Name);

        foreach (var permissionGroupRecord in permissionGroupRecords)
        {
            var permissionGroupRecordInDatabase = permissionGroupRecordsInDatabase.GetOrDefault(permissionGroupRecord.Name);
            if (permissionGroupRecordInDatabase == null)
            {
                /* New group */
                newRecords.Add(permissionGroupRecord);
                continue;
            }

            if (permissionGroupRecord.HasSameData(permissionGroupRecordInDatabase))
            {
                /* Not changed */
                continue;
            }

            /* Changed */
            permissionGroupRecordInDatabase.Patch(permissionGroupRecord);
            changedRecords.Add(permissionGroupRecordInDatabase);
        }
        
        /* Deleted */
        var deletedRecords = permissionGroupRecordsInDatabase.Values
            .Where(x => PermissionManagementOptions.DeletedPermissionGroups.Contains(x.Name))
            .ToArray();

        await PermissionGroupRepository.InsertManyAsync(newRecords);
        await PermissionGroupRepository.UpdateManyAsync(changedRecords);
        await PermissionGroupRepository.DeleteManyAsync(deletedRecords);
    }

    private string GetDistributedLockKey()
    {
        return $"{ApplicationNameAccessor.ApplicationName}_AbpPermissionUpdateLock";
    }

    private string GetCacheKey()
    {
        return $"{CacheOptions.KeyPrefix}_{ApplicationNameAccessor.ApplicationName}_AbpPermissionsHash";
    }

    private static string CalculateHash(
        PermissionGroupDefinitionRecord[] permissionGroupRecords,
        PermissionDefinitionRecord[] permissionRecords,
        IEnumerable<string> deletedPermissionGroups,
        IEnumerable<string> deletedPermissions)
    {
        var stringBuilder = new StringBuilder();
        
        stringBuilder.Append("PermissionGroupRecords:");
        stringBuilder.AppendLine(JsonSerializer.Serialize(permissionGroupRecords));
        
        stringBuilder.Append("PermissionRecords:");
        stringBuilder.AppendLine(JsonSerializer.Serialize(permissionRecords));
        
        stringBuilder.Append("DeletedPermissionGroups:");
        stringBuilder.AppendLine(deletedPermissionGroups.JoinAsString(","));
        
        stringBuilder.Append("DeletedPermission:");
        stringBuilder.Append(deletedPermissions.JoinAsString(","));
        
        return stringBuilder
            .ToString()
            .ToMd5();
    }
}