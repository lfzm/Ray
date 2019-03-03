﻿using System.Threading.Tasks;
using Dapper;
using Ray.Core.Snapshot;
using Ray.Core.Storage;

namespace Ray.Storage.PostgreSQL
{
    public class FollowSnapshotStorage<PrimaryKey> : IFollowSnapshotStorage<PrimaryKey>
    {
        readonly FollowStorageConfig config;
        private readonly string deleteSql;
        private readonly string getByIdSql;
        private readonly string insertSql;
        private readonly string updateSql;
        private readonly string updateStartTimestampSql;
        public FollowSnapshotStorage(FollowStorageConfig config)
        {
            this.config = config;
            var followStateTable = config.FollowSnapshotTable;
            deleteSql = $"DELETE FROM {followStateTable} where stateid=@StateId";
            getByIdSql = $"select * FROM {followStateTable} where stateid=@StateId";
            insertSql = $"INSERT into {followStateTable}(stateid,version,StartTimestamp)VALUES(@StateId,@Version,@StartTimestamp)";
            updateSql = $"update {followStateTable} set version=@Version,StartTimestamp=@StartTimestamp where stateid=@StateId";
            updateStartTimestampSql = $"update {followStateTable} set StartTimestamp=@StartTimestamp where stateid=@StateId";
        }
        public async Task<FollowSnapshot<PrimaryKey>> Get(PrimaryKey id)
        {
            using (var conn = (config.Config as StorageConfig).CreateConnection())
            {
                var data = await conn.QuerySingleOrDefaultAsync<FollowSnapshot>(getByIdSql, new { StateId = id.ToString() });
                if (data != default)
                {
                    return new FollowSnapshot<PrimaryKey>()
                    {
                        StateId = id,
                        Version = data.Version,
                        DoingVersion = data.Version,
                        StartTimestamp = data.StartTimestamp
                    };
                }
            }
            return default;
        }
        public async Task Insert(FollowSnapshot<PrimaryKey> snapshot)
        {
            using (var connection = (config.Config as StorageConfig).CreateConnection())
            {
                await connection.ExecuteAsync(insertSql, new
                {
                    StateId = snapshot.StateId.ToString(),
                    snapshot.Version,
                    snapshot.StartTimestamp
                });
            }
        }

        public async Task Update(FollowSnapshot<PrimaryKey> snapshot)
        {
            using (var connection = (config.Config as StorageConfig).CreateConnection())
            {
                await connection.ExecuteAsync(updateSql, new
                {
                    StateId = snapshot.StateId.ToString(),
                    snapshot.Version,
                    snapshot.StartTimestamp
                });
            }
        }
        public async Task Delete(PrimaryKey id)
        {
            using (var conn = (config.Config as StorageConfig).CreateConnection())
            {
                await conn.ExecuteAsync(deleteSql, new { StateId = id.ToString() });
            }
        }

        public async Task UpdateStartTimestamp(PrimaryKey id, long timestamp)
        {
            using (var connection = (config.Config as StorageConfig).CreateConnection())
            {
                await connection.ExecuteAsync(updateStartTimestampSql, new
                {
                    StateId = id.ToString(),
                    StartTimestamp = timestamp
                });
            }
        }
    }
}
