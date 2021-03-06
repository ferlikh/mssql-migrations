﻿using Migrate.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.IO;
using System.Data;
using System.Text.Json;
using ObjectConfiguration = Migrate.Models.AppSettings.MigrationSettings.ObjectConfiguration;
using System.Text.RegularExpressions;

namespace Migrate
{
    public enum SQLTypes
    {
        [Description("U")]
        UserTable,
        [Description("PK")]
        PrimaryKey,
        [Description("F")]
        ForeignKey,
        [Description("D")]
        DefaultConstraint,
        [Description("UQ")]
        UniqueConstraint,
        [Description("C")]
        CheckConstraint,
        [Description("FN")]
        ScalarFunction,
        [Description("IF")]
        InlineTableFunction,
        [Description("TF")]
        TableFunction,
        [Description("V")]
        View,
        [Description("P")]
        StoredProcedure,

    }

    class Migrator
    {
        private AppSettings settings;
        private readonly string path;
        private readonly string sourceConn;
        private readonly string targetConn;
        private string sourceDb;
        private string targetDb;
        private int sourceId;
        private int targetId;

        private HashSet<string> modelSchemas; // schemas to model objects
        private HashSet<string> dataSchemas; // schemas to copy the data from

        // maps qualified_name to object_id
        private Dictionary<string, string> sourceObjects = new Dictionary<string, string>();
        private Dictionary<string, string> targetObjects = new Dictionary<string, string>();

        // maps object_id to type
        private Dictionary<string, string> sourceTypes = new Dictionary<string, string>();
        private Dictionary<string, string> targetTypes = new Dictionary<string, string>();

        // maps object_id to the DateTime of last UPDATE
        private Dictionary<string, DateTime> sourceUpdates;
        private Dictionary<string, DateTime> targetUpdates;

        private Dictionary<string, int> sourceSchemas;
        private Dictionary<string, int> targetSchemas;
        private Dictionary<string, SysTable> sourceTables;
        private Dictionary<string, SysTable> targetTables;
        private Dictionary<string, List<SysColumn>> sourceColumns;
        private Dictionary<string, List<SysColumn>> targetColumns;
        private Dictionary<string, SysForeignKey> sourceKeys;
        private Dictionary<string, SysForeignKey> targetKeys;
        private Dictionary<string, SysConstraint> sourceConstraints;
        private Dictionary<string, SysConstraint> targetConstraints;
        private Dictionary<string, SysIndex> sourceIndexes;
        private Dictionary<string, SysIndex> targetIndexes;
        private Dictionary<string, SysTableType> sourceTableTypes;
        private Dictionary<string, SysTableType> targetTableTypes;
        private Dictionary<string, SysObject> sourceFuncs;
        private Dictionary<string, SysObject> targetFuncs;
        private Dictionary<string, SysObject> sourceViews;
        private Dictionary<string, SysObject> targetViews;
        private Dictionary<string, SysObject> sourceProcs;
        private Dictionary<string, SysObject> targetProcs;
        private List<SysDependency> sourceDependencies;

        private DbData dbData = new DbData();
        private Dictionary<SysTable, RowCollection> dbUpdates;
        private Dictionary<string, HashSet<string>> dbDependencies;
        private Dictionary<string, bool> isGenerated;

        private List<int> dataSchemaIds
        {
            get
            {
                return sourceSchemas.Keys.Where(schema => dataSchemas.Contains(schema)).Select(schema => sourceSchemas[schema]).ToList();
            }
        }
        private List<int> sourceSchemaIds
        {
            get
            {
                return sourceSchemas.Values.ToList();
            }
        }
        private List<int> targetSchemaIds
        {
            get
            {
                return targetSchemas.Values.ToList();
            }
        }
        private bool usingSourceFile
        {
            get
            {
                return !string.IsNullOrEmpty(settings.Path) && string.IsNullOrEmpty(settings.ConnectionStrings.Source);
            }
        }
        private List<string> dataTableIncludes
        {
            get { return settings.Data?.Tables?.Include ?? new List<string>(); }
        }
        private List<string> dataTableExcludes
        {
            get { return settings.Data?.Tables?.Exclude ?? new List<string>(); }
        }
        // list of tables whose data is to be synce
        private List<SysTable> dataTables
        {
            get
            {
                var qualifiedIncludes = QualifyNames(dataTableIncludes);
                var qualifiedExcludes = QualifyNames(dataTableExcludes);

                return sourceTables.Values
                    .Where(table => !qualifiedExcludes.Contains(table.qualified_name) &&
                        (dataSchemas.Contains(table.schema_name) || qualifiedIncludes.Contains(table.qualified_name)))
                    .ToList();
            }
        }
        // Takes two connection strings
        public Migrator(AppSettings config)
        {
            if(config?.ConnectionStrings == null)
            {
                throw new Exception("connection string not specified");
            }
            settings = config;
            sourceConn = config.ConnectionStrings.Source;
            targetConn = config.ConnectionStrings.Target;

            if (string.IsNullOrEmpty(settings.Path))
            {
                settings.Path = Helpers.GetAppRootDir();
            }
            path = settings.Path;

            if (usingSourceFile)
            {
                ReadModel();
                ReadData();
            }
            else
            {
                sourceDb = Helpers.GetDbName(sourceConn);
                sourceId = GetDatabaseId(sourceDb, sourceConn);
            }

            targetDb = Helpers.GetDbName(targetConn);
            targetId = GetDatabaseId(targetDb, targetConn);

            if (sourceId == -1)
            {
                throw new Exception("source id could not be read");
            }
            else if (targetId == -1)
            {
                throw new Exception("target id could not be read");
            }
        }

        public void Migrate()
        {
            Load();
            Patch();
            Create();
            Seed();
            Update();
            Alter();
            Dump();
        }

        protected void Load()
        {
            if(usingSourceFile)
            {
                modelSchemas = sourceTables.Values.Select(table => table.schema_name).ToHashSet();
                dataSchemas = dbData.Seed.Keys // seed is guaranteed to include all data tables
                    .Select(key => key.Split(".")[0])
                    .Distinct()
                    .Select(schema => Regex.Replace(schema, @"\[(\w+)\]", "$1"))
                    .ToHashSet();

                LoadTargetModels();

                return;
            }

            var allSchemas = Helpers.ExecuteCommand<NameValue>(new SqlCommand("select name from sys.schemas"), sourceConn)
                    .Select(schema => schema.name).ToList();
            dataSchemas = ConfigureSchemas(settings.Data?.Schemas, allSchemas);
            modelSchemas = ConfigureSchemas(settings.Model?.Schemas, allSchemas);

            var schemas = dataSchemas.ToList();
            schemas.AddRange(modelSchemas.ToList());
            schemas = schemas.Distinct().ToList();

            if (schemas.Count() == 0) throw new Exception("no schemas to load");

            LoadSchemas(schemas);
            LoadTables();
            LoadColumns();
            LoadConstraints();
            LoadKeys();
            LoadIndexes();
            LoadTableTypes();
            LoadFunctions();
            LoadProcedures();
            LoadViews();
            LoadDependencies();
            MapModels();
        }

        protected void Dump()
        {
            if (!settings.ToJSON) return;

            WriteModel();
            WriteData();
        }

        protected void Create()
        {
            var builder = new StringBuilder();
            builder.Append($"USE {targetDb};\n\n");

            foreach (var schema in sourceSchemas.Keys)
            {
                builder.Append(Query.CreateSchema(schema));
            }
            foreach (var tt in sourceTableTypes.Values)
            {
                builder.Append(Query.CreateTableType(tt));
            }
            foreach (var func in sourceFuncs.Values.Where(f => f.type != SQLTypes.InlineTableFunction.Description()))
            {
                builder.Append(Query.CreateFunction(func));
            }
            foreach (var table in sourceTables.Values)
            {
                builder.Append(Query.CreateTable(table, sourceColumns[table.object_id]));
            }
            foreach (var index in sourceIndexes.Values)
            {
                builder.Append(Query.CreateIndex(index));
            }
            
            foreach (var func in sourceFuncs.Values.Where(f => f.type == SQLTypes.InlineTableFunction.Description()))
            {
                RecursiveCreate(builder, func);
            }
            foreach (var view in sourceViews.Values)
            {
                RecursiveCreate(builder, view);
            }
            foreach (var proc in sourceProcs.Values)
            {
                RecursiveCreate(builder, proc);
            }

            File.WriteAllText($"{path}\\create.sql", builder.ToString(), Encoding.UTF8);
        }

        private void RecursiveCreate(StringBuilder builder, SysObject @object) {
            if(isGenerated[@object.object_id])
            {
                return;
            }
            else if(dbDependencies.ContainsKey(@object.object_id))
            {
                var dependencies = dbDependencies[@object.object_id].Select(id =>
                {
                    var type = sourceTypes[id];
                    if (type == SQLTypes.InlineTableFunction.Description())
                    {
                        return sourceFuncs[id];
                    }
                    else if (type == SQLTypes.View.Description())
                    {
                        return sourceViews[id];
                    }
                    else if (type == SQLTypes.StoredProcedure.Description())
                    {
                        return sourceProcs[id];
                    }

                    return null; // should not get here
                });
                foreach(var dep in dependencies)
                {
                    RecursiveCreate(builder, dep);
                }
            }

            CreateByType(builder, @object);
            isGenerated[@object.object_id] = true;
        }
        private void CreateByType(StringBuilder builder, SysObject @object)
        {

            string query = null;
            if(@object.type == SQLTypes.InlineTableFunction.Description())
            {
                query = Query.CreateFunction(@object);
            }
            else if(@object.type == SQLTypes.View.Description())
            {
                query = Query.CreateView(@object);
            }
            else if (@object.type == SQLTypes.StoredProcedure.Description())
            {
                query = Query.CreateProc(@object);
            }
            if(query != null) builder.Append(query);
        }

        protected void Alter()
        {
            var builder = new StringBuilder();
            builder.Append($"USE {targetDb};\n\n");

            // constraints
            var allConstraints = sourceConstraints.Values;
            var primaryKeys = allConstraints.Where(c => c.type == Enumerations.GetDescription(SQLTypes.PrimaryKey));
            var defaultConstraints = allConstraints.Where(c => c.type == Enumerations.GetDescription(SQLTypes.DefaultConstraint));
            var uniqueConstraints = allConstraints.Where(c => c.type == Enumerations.GetDescription(SQLTypes.UniqueConstraint));
            var checkConstraints = allConstraints.Where(c => c.type == Enumerations.GetDescription(SQLTypes.CheckConstraint));

            foreach (var key in primaryKeys)
            {
                builder.Append(Query.AddPrimaryKey(key));
                builder.Append(Query.BatchSeperator);
            }

            foreach (var constraint in defaultConstraints)
            {
                builder.Append(Query.AddDefaultConstraint(constraint));
                builder.Append(Query.BatchSeperator);
            }

            foreach (var constraint in uniqueConstraints)
            {
                builder.Append(Query.AddUniqueConstraint(constraint));
                builder.Append(Query.BatchSeperator);
            }

            foreach (var constraint in checkConstraints)
            {
                builder.Append(Query.AddCheckConstraint(constraint));
                builder.Append(Query.BatchSeperator);
            }

            // foreign keys
            foreach (var key in sourceKeys.Values)
            {
                builder.Append(Query.AddForeignKey(key));
                builder.Append(Query.BatchSeperator);
            }

            // views
            foreach (var view in sourceViews.Values)
            {
                //if (!ObjectChanged(view, targetViews)) continue;
                builder.Append(Query.AlterView(view));
            }
            // funcs
            foreach (var func in sourceFuncs.Values)
            {
                //if (!ObjectChanged(func, targetFuncs)) continue;
                builder.Append(Query.AlterFunc(func));
            }
            // procs
            foreach (var proc in sourceProcs.Values)
            {
                //if (!ObjectChanged(proc, targetProcs)) continue;
                builder.Append(Query.AlterProc(proc));
            }

            File.WriteAllText($"{path}\\alter.sql", builder.ToString(), Encoding.UTF8);
        }

        protected void Seed()
        {
            var builder = new StringBuilder();
            builder.Append($"USE {targetDb};\n\n");
            builder.Append(Query.ToggleIdInsertForEach("OFF"));
            builder.Append(Query.ToggleConstraintForEach(false) + "\n\n");

            var tables = dataTables;

            foreach (var table in tables)
            {
                var columns = sourceColumns[table.object_id];
                var rows = GetTableData(table);

                // only seed tables w/ primary keys
                if (columns.All(c => c.primary_key == null)) continue;
                // if nothing to seed
                else if (rows == null || rows.Count() == 0) continue;

                SeedTable(builder, table, columns, rows);

                if(settings.ToJSON)
                {
                    AddData(dbData.Seed, table, rows);
                }
            }

            // if there is an update, this code will run there
            if(!GetTablesToUpdate().Any())
            {
                builder.Append(Query.ToggleConstraintForEach(true));
            }

            File.WriteAllText($"{path}\\seed.sql", builder.ToString(), Encoding.UTF8);
        }

        private List<string> QualifyNames(List<string> collection)
        {
            return collection.Select(name =>
            {
                var parts = name.Split(".");
                var schema = parts[0];
                var table = parts[1];
                return $"[{schema}].[{table}]";
            }).ToList();
        }

        private RowCollection GetTableData(SysTable table)
        {
            if(usingSourceFile)
            {
                return dbData.Seed.GetValueOrDefault(table.qualified_name);
            }
            var rows = Helpers.ExecuteCommandToDictionary(new SqlCommand($"select * from {table.qualified_name}"), sourceConn);
            return Query.ConvertRows(rows);
        }

        private Dictionary<SysTable, RowCollection> GetTablesToUpdate()
        {
            if (dbUpdates != null)
                return dbUpdates;

            dbUpdates = new Dictionary<SysTable, RowCollection>();
            if(sourceUpdates == null) sourceUpdates = GetLastTableUpdates(sourceTables.Values);
            if(targetUpdates == null) targetUpdates = GetLastTableUpdates(targetTables.Values);

            foreach (var table in dataTables)
            {
                if (!sourceUpdates.ContainsKey(table.object_id)) continue; // ignore this table
                else if (!targetObjects.ContainsKey(table.qualified_name)) continue; // target hasn't created this table

                var isTargetEmpty = IsTargetEmpty(table);
                if (!targetUpdates.ContainsKey(targetObjects[table.qualified_name]) && isTargetEmpty) continue; // target has never been updated + is empty, should seed, update would be slow

                var sourceUpdate = sourceUpdates[table.object_id];
                var targetUpdate = targetUpdates.ContainsKey(targetObjects[table.qualified_name])? 
                    targetUpdates[targetObjects[table.qualified_name]]: (DateTime?)null;

                if (targetUpdate == null || targetUpdate <= sourceUpdate)
                {
                    var columns = sourceColumns[table.object_id];
                    var rows = GetTableData(table);

                    // only perform update on tables w/ primary keys
                    if (columns.All(c => c.primary_key == null)) continue;
                    // don't update on empty
                    else if (rows == null || rows.Count() == 0 || isTargetEmpty) continue;

                    dbUpdates.Add(table, rows);
                    
                    if (settings.ToJSON)
                    {
                        AddData(dbData.Update, table, rows);
                    }
                }
            }

            return dbUpdates;
        }

        private bool IsTargetEmpty(SysTable table)
        {
            return Helpers
                .ExecuteCommand<bool>(
                    new SqlCommand($"select case when not exists (select top(1) * from {table.qualified_name}) then cast(1 as bit) else cast(0 as bit) end"),
                    targetConn)
                .FirstOrDefault();
        }

        protected void Update()
        {
            var builder = new StringBuilder();
            builder.Append($"USE {targetDb};\n\n");

            var toUpdate = GetTablesToUpdate();

            if (toUpdate.Any())
            {
                builder.Append(Query.ToggleIdInsertForEach("OFF"));
                builder.Append(Query.ToggleConstraintForEach(false));
                builder.Append(Query.SetNoCount("ON"));
                builder.Append($"\n{Query.BatchSeperator}");
            }

            foreach (var pair in toUpdate)
            {
                var table = pair.Key;
                var columns = sourceColumns[table.object_id];
                var rows = pair.Value;

                UpdateTable(builder, table, columns, rows);
            }

            if (toUpdate.Any())
            {
                builder.Append("\n" + Query.ToggleConstraintForEach(true));
            }
            else
            {
                builder.Append("-- Nothing to update. --");
            }

            File.WriteAllText($"{path}\\update.sql", builder.ToString(), Encoding.UTF8);
        }

        protected void Patch()
        {
            var builder = new StringBuilder();
            var opening = $"USE {targetDb};\n\n";
            builder.Append(opening);

            // drop any index not in the source or different
            var indexesToDrop = targetIndexes.Values.Where(i =>
            {
                var sourceIndex = sourceIndexes.Values.Where(x => x.name == i.name).FirstOrDefault();
                if (sourceIndex == null) return true;
                else
                {
                    return sourceIndex.qualified_table_name != i.qualified_table_name
                    || sourceIndex.columns != i.columns
                    || sourceIndex.include != i.include;
                }
            });

            foreach(var index in indexesToDrop)
            {
                builder.Append(Query.DropIndex(index));
                builder.Append(Query.BatchSeperator);
            }

            var constraintsToDrop = targetConstraints.Values.Where(targetConstraint =>
            {
                // ignore all primary keys until you can drop the table and its dependencies
                if (targetConstraint.type == Enumerations.GetDescription(SQLTypes.PrimaryKey)) return false;

                var sourceConstraint = sourceObjects.ContainsKey(targetConstraint.qualified_name) ? sourceConstraints[sourceObjects[targetConstraint.qualified_name]] : null;

                if (sourceConstraint == null) return true;
                else return sourceConstraint.columns != targetConstraint.columns;
            });

            foreach(var constraint in constraintsToDrop)
            {
                builder.Append(Query.DropConstraint(constraint));
                builder.Append(Query.BatchSeperator);
            }

            // drop foreign keys not in source or different
            var keysToDrop = targetKeys.Values.Where(k => {
                if (!sourceObjects.ContainsKey(k.qualified_name)) return true;
                else
                {
                    var fkey = sourceKeys[sourceObjects[k.qualified_name]];
                    return
                        fkey.qualified_parent_table != k.qualified_parent_table
                        || fkey.qualified_referenced_table != k.qualified_referenced_table
                        || fkey.referenced_column != k.referenced_column
                        || fkey.parent_column != k.parent_column;
                }
            });

            foreach (var key in keysToDrop)
            {
                builder.Append(Query.DropForeignKey(key));
                builder.Append(Query.BatchSeperator);
            }

            var excludedTables = targetTables.Values
                .Select(k => k.qualified_name)
                .Except(sourceTables.Values.Select(k => k.qualified_name));

            var tablesToDrop = targetTables.Values.Where(k => excludedTables.Contains(k.qualified_name));
            foreach (var table in tablesToDrop)
            {
                builder.Append(Query.DropTable(table));
                builder.Append(Query.BatchSeperator);
            }

            var schemasToDrop = targetSchemas.Keys.Except(sourceSchemas.Keys);
            foreach(var schema in schemasToDrop)
            {
                builder.Append(Query.DropSchema(schema));
                builder.Append(Query.BatchSeperator);
            }

            PatchTemporalTables(builder);
            PatchColumns(builder);

            if (builder.ToString() == opening)
            {
                builder.Append("-- Nothing to patch. --");
            }

            File.WriteAllText($"{path}\\patch.sql", builder.ToString(), Encoding.UTF8);
        }

        private void PatchTemporalTables(StringBuilder builder)
        {
            var temporalTablesAdd = new List<SysTable>();
            var temporalTablesDrop = new List<SysTable>();
            var temporalTablesSet = new List<SysTable>();

            foreach (var targetTable in targetTables.Values)
            {
                var sourceTable = sourceObjects.ContainsKey(targetTable.qualified_name) ? sourceTables[sourceObjects[targetTable.qualified_name]] : null;

                if (sourceTable == null) continue;

                if (string.IsNullOrEmpty(targetTable.history_table) && !string.IsNullOrEmpty(sourceTable.history_table))
                {
                    temporalTablesAdd.Add(sourceTable);
                }
                else if (!string.IsNullOrEmpty(targetTable.history_table))
                {
                    if (string.IsNullOrEmpty(sourceTable.history_table))
                    {
                        temporalTablesDrop.Add(sourceTable);
                    }
                    else if (sourceTable.history_table != targetTable.history_table)
                    {
                        temporalTablesSet.Add(sourceTable);
                    }
                }
            }

            foreach (var table in temporalTablesAdd)
            {
                builder.Append(Query.AddSystemVersioning(table));
                builder.Append(Query.BatchSeperator);
            }
            foreach (var table in temporalTablesDrop)
            {
                builder.Append(Query.DropSystemVersioning(table));
                builder.Append(Query.BatchSeperator);
            }
            foreach (var table in temporalTablesSet)
            {
                builder.Append(Query.SetSystemVersioning(table, true));
                builder.Append(Query.BatchSeperator);
            }
        }

        // drop/add/alter any columns
        private void PatchColumns(StringBuilder builder)
        {
            var source = sourceObjects.Keys.ToHashSet();
            var target = targetObjects.Keys.ToHashSet();
            var intersect = source.Intersect(target).Where(name => sourceTypes[sourceObjects[name]] == Enumerations.GetDescription(SQLTypes.UserTable));

            foreach (var tableName in intersect)
            {
                var sourceId = sourceObjects[tableName];
                var targetId = targetObjects[tableName];
                var sourceTable = sourceTables[sourceId];

                var sourceCols = sourceColumns[sourceId].Where(c => c.generated_always_type == "0");
                var targetCols = targetColumns[targetId].Where(c => c.generated_always_type == "0" || string.IsNullOrEmpty(sourceTable.history_table));

                var sourceColDictionary = sourceCols.ToDictionary(c => c.name);
                var targetColDictionary = targetCols.ToDictionary(c => c.name);

                var toDrop = targetCols.Where(c => !sourceColDictionary.ContainsKey(c.name)).ToList();
                var toAdd = sourceCols.Where(c => !targetColDictionary.ContainsKey(c.name)).ToList();
                var toAlter = sourceCols.Where(source =>
                {
                    if (!targetColDictionary.ContainsKey(source.name)) return false;
                    var target = targetColDictionary[source.name];
                    return source.type_definition != target.type_definition;
                }).ToList();

                if (toDrop.Any() || toAdd.Any() || toAlter.Any())
                {
                    builder.Append($"-- {tableName} --\n");

                    var targetObjConstraints = targetConstraints.Values.Where(c => c.parent_object_id == targetId);
                    var primaryKey = targetObjConstraints.Where(c => c.type == Enumerations.GetDescription(SQLTypes.PrimaryKey)).FirstOrDefault();

                    toDrop.ForEach(column =>
                    {
                        builder.Append(Query.DropColumn(column));
                        builder.Append(Query.BatchSeperator);
                    });
                    toAdd.ForEach(column =>
                    {
                        if (column.isNullable)
                        {
                            builder.Append(Query.AddColumn(column));
                            builder.Append(Query.BatchSeperator);
                        }
                        else
                        {
                            var nullable = SysColumn.DeepClone(column);
                            nullable.is_nullable = "true";
                            builder.Append(Query.AddColumn(nullable));
                            builder.Append(Query.BatchSeperator);
                            builder.Append(Query.UpdateIfNull(column));
                            builder.Append(Query.BatchSeperator);
                            toAlter.Add(column);
                        }
                    });
                    toAlter.ForEach(column =>
                    {
                        if(DependsOn(primaryKey, column)) {
                            builder.Append(Query.DropPrimaryKey(primaryKey));
                            builder.Append(Query.BatchSeperator);
                        }
                        builder.Append(Query.AlterColumn(column));
                        builder.Append(Query.BatchSeperator);
                    });
                }
            }
        }

        //private Dictionary<string, DateTime> GetLastTableUpdates(string conn)
        //{
        //    var cmd = new SqlCommand(Query.GetLastUpdate());
        //    return Helpers.ExecuteCommand<DateValue>(cmd, conn).ToDictionary(u => u.id, u => u.date);
        //}

        private Dictionary<string, DateTime> GetLastTableUpdates(IEnumerable<SysTable> tables)
        {
            return tables.Where(t => t.update_date.HasValue).ToDictionary(t => t.object_id, t => (DateTime)t.update_date);
        }

        private void AddData(DataDictionary dict, SysTable table, RowCollection rows)
        {
            if (!dict.ContainsKey(table.qualified_name))
            {
                dict.Add(table.qualified_name, rows);
            }
        }

        private void UpdateTable(StringBuilder builder, SysTable table, List<SysColumn> columns, RowCollection rows)
        {
            var hasIdentity = table.has_identity != null;
            builder.Append($"-- {table.qualified_name} --\n\n");

            if (hasIdentity) builder.Append(Query.ToggleIdInsert(table.qualified_name, "ON"));

            foreach(var row in rows)
            {
                builder.Append(Query.UpdateIf(table, columns, row));
                builder.Append(Query.BatchSeperator);
            }

            if (hasIdentity) builder.Append(Query.ToggleIdInsert(table.qualified_name, "OFF") + "\n");
        }

        private void SeedTable(StringBuilder builder, SysTable table, List<SysColumn> columns, RowCollection rows)
        {
            var hasIdentity = table.has_identity != null;

            builder.Append($"-- {table.qualified_name} --\n\n");
            if (hasIdentity) builder.Append(Query.ToggleIdInsert(table.qualified_name, "ON"));
            builder.Append(Query.SeedIf(table, columns, rows));
            if (hasIdentity) builder.Append(Query.ToggleIdInsert(table.qualified_name, "OFF"));
            builder.Append(Query.BatchSeperator);
        }

        private bool DependsOn(SysConstraint constraint, SysColumn column)
        {
            if (constraint == null) return false;
            else if (constraint.columns.Contains(",") && constraint.columns.Split(",").Contains(column.name)) 
                return true;
            else if (constraint.columns == column.name) return true;

            return false;
        }

        private int GetDatabaseId(string name, string conn)
        {
            var cmd = new SqlCommand($"select database_id from sys.databases where name = '{name}'");
            var record = Helpers.ExecuteCommandToDictionary(cmd, conn).FirstOrDefault();
            if (record == null)
            {
                return -1;
            }
            return (int)record["database_id"];
        }

        private HashSet<string> ConfigureSchemas(ObjectConfiguration configuration, List<string> allSchemas)
        {
            if (configuration == null)
            {
                return new HashSet<string>();
            }
            else
            {
                var schemas = new List<string>();
                if (configuration.Include != null)
                {
                    schemas.AddRange(configuration.Include);
                }
                else if (configuration.Exclude != null)
                {
                    schemas.AddRange(allSchemas.Except(configuration.Exclude));
                }
                return schemas.ToHashSet();
            }
        }

        private void LoadSchemas(IEnumerable<string> schemas)
        {
            var cmd = new SqlCommand($"select schema_id[id], name from sys.schemas where name in ({string.Join(", ", schemas.Select(schema => $"'{schema}'"))})");
            sourceSchemas = Helpers.ExecuteCommand<IdValue>(cmd, sourceConn).ToDictionary(s => s.name, s => s.id);
            targetSchemas = Helpers.ExecuteCommand<IdValue>(cmd, targetConn).ToDictionary(s => s.name, s => s.id);
        }

        private void LoadTables()
        {
            var sourceCmd = new SqlCommand(Query.GetTables(sourceSchemaIds, settings.Data?.Tables?.Include));
            var targetCmd = new SqlCommand(Query.GetTables(targetSchemaIds));
            sourceTables = Helpers.ExecuteCommand<SysTable>(sourceCmd, sourceConn).ToDictionary(t => t.object_id);
            targetTables = Helpers.ExecuteCommand<SysTable>(targetCmd, targetConn).ToDictionary(t => t.object_id);
        }

        private void LoadColumns()
        {
            var sourceCmd = new SqlCommand(Query.GetColumns(sourceSchemaIds));
            var targetCmd = new SqlCommand(Query.GetColumns(targetSchemaIds));
            var allSourceColumns = Helpers.ExecuteCommand<SysColumn>(sourceCmd, sourceConn);
            var allTargetColumns = Helpers.ExecuteCommand<SysColumn>(targetCmd, targetConn);

            sourceColumns = allSourceColumns.GroupBy(c => c.object_id).ToDictionary(c => c.Key, c => c.ToList());
            targetColumns = allTargetColumns.GroupBy(c => c.object_id).ToDictionary(c => c.Key, c => c.ToList());
        }

        private void LoadKeys()
        {
            var sourceCmd = new SqlCommand(Query.GetForeignKeys(sourceSchemaIds));
            var targetCmd = new SqlCommand(Query.GetForeignKeys(targetSchemaIds));
            sourceKeys = Helpers.ExecuteCommand<SysForeignKey>(sourceCmd, sourceConn).ToDictionary(k => k.constraint_id);
            targetKeys = Helpers.ExecuteCommand<SysForeignKey>(targetCmd, targetConn).ToDictionary(k => k.constraint_id);
        }

        private void LoadIndexes()
        {
            var sourceCmd = new SqlCommand(Query.GetIndexes(sourceSchemaIds));
            var targetCmd = new SqlCommand(Query.GetIndexes(targetSchemaIds));
            sourceIndexes = Helpers.ExecuteCommand<SysIndex>(sourceCmd, sourceConn).ToDictionary(p => p.object_id + p.index_id);
            targetIndexes = Helpers.ExecuteCommand<SysIndex>(targetCmd, targetConn).ToDictionary(p => p.object_id + p.index_id);
        }

        private void LoadTableTypes()
        {
            var sourceCmd = new SqlCommand(Query.GetTableTypes(sourceSchemaIds));
            var targetCmd = new SqlCommand(Query.GetTableTypes(targetSchemaIds));
            sourceTableTypes = Helpers.ExecuteCommand<SysTableType>(sourceCmd, sourceConn).ToDictionary(p => p.object_id);
            targetTableTypes = Helpers.ExecuteCommand<SysTableType>(targetCmd, targetConn).ToDictionary(p => p.object_id);
        }

        private void LoadConstraints()
        {
            var sourceCmd = new SqlCommand(Query.GetConstraints(sourceSchemaIds));
            var targetCmd = new SqlCommand(Query.GetConstraints(targetSchemaIds));
            sourceConstraints = Helpers.ExecuteCommand<SysConstraint>(sourceCmd, sourceConn).ToDictionary(p => p.object_id);
            targetConstraints = Helpers.ExecuteCommand<SysConstraint>(targetCmd, targetConn).ToDictionary(p => p.object_id);
        }

        private void LoadFunctions()
        {
            var sourceCmd = new SqlCommand(Query.GetFunctions(sourceSchemaIds));
            var targetCmd = new SqlCommand(Query.GetFunctions(targetSchemaIds));
            sourceFuncs = Helpers.ExecuteCommand<SysObject>(sourceCmd, sourceConn).ToDictionary(p => p.object_id);
            targetFuncs = Helpers.ExecuteCommand<SysObject>(targetCmd, targetConn).ToDictionary(p => p.object_id);
        }

        private void LoadProcedures()
        {
            var sourceCmd = new SqlCommand(Query.GetStoredProcedures(sourceSchemaIds));
            var targetCmd = new SqlCommand(Query.GetStoredProcedures(targetSchemaIds));
            sourceProcs = Helpers.ExecuteCommand<SysObject>(sourceCmd, sourceConn).ToDictionary(p => p.object_id);
            targetProcs = Helpers.ExecuteCommand<SysObject>(targetCmd, targetConn).ToDictionary(p => p.object_id);
        }

        private void LoadViews()
        {
            var sourceCmd = new SqlCommand(Query.GetViews(sourceSchemaIds));
            var targetCmd = new SqlCommand(Query.GetViews(targetSchemaIds));
            sourceViews = Helpers.ExecuteCommand<SysObject>(sourceCmd, sourceConn).ToDictionary(p => p.object_id);
            targetViews = Helpers.ExecuteCommand<SysObject>(targetCmd, targetConn).ToDictionary(p => p.object_id);
        }

        private void LoadTargetModels()
        {
            SqlCommand schemaCmd = new SqlCommand(@$"select schema_id[id], name from sys.schemas where name in ({
                string.Join(", ", sourceSchemas.Keys.Select(schema => $"'{schema}'"))
            })");
            targetSchemas = Helpers.ExecuteCommand<IdValue>(schemaCmd, targetConn).ToDictionary(s => s.name, s => s.id);

            SqlCommand tableCmd = new SqlCommand(Query.GetTables(targetSchemaIds));
            SqlCommand columnCmd = new SqlCommand(Query.GetColumns(targetSchemaIds));
            SqlCommand keyCmd = new SqlCommand(Query.GetForeignKeys(targetSchemaIds));
            SqlCommand constraintCmd = new SqlCommand(Query.GetConstraints(targetSchemaIds));
            SqlCommand indexCmd = new SqlCommand(Query.GetIndexes(targetSchemaIds));
            SqlCommand tableTypeCmd = new SqlCommand(Query.GetTableTypes(targetSchemaIds));
            SqlCommand fnCmd = new SqlCommand(Query.GetFunctions(targetSchemaIds));
            SqlCommand spCmd = new SqlCommand(Query.GetStoredProcedures(targetSchemaIds));
            SqlCommand vwCmd = new SqlCommand(Query.GetViews(targetSchemaIds));
            targetTables = Helpers.ExecuteCommand<SysTable>(tableCmd, targetConn).ToDictionary(t => t.object_id);
            var allTargetColumns = Helpers.ExecuteCommand<SysColumn>(columnCmd, targetConn);
            targetColumns = allTargetColumns.GroupBy(c => c.object_id).ToDictionary(c => c.Key, c => c.ToList());
            targetKeys = Helpers.ExecuteCommand<SysForeignKey>(keyCmd, targetConn).ToDictionary(k => k.constraint_id);
            targetConstraints = Helpers.ExecuteCommand<SysConstraint>(constraintCmd, targetConn).ToDictionary(p => p.object_id);
            targetIndexes = Helpers.ExecuteCommand<SysIndex>(indexCmd, targetConn).ToDictionary(p => p.object_id + p.index_id);
            targetTableTypes = Helpers.ExecuteCommand<SysTableType>(tableTypeCmd, targetConn).ToDictionary(p => p.object_id);
            targetFuncs = Helpers.ExecuteCommand<SysObject>(fnCmd, targetConn).ToDictionary(p => p.object_id);
            targetProcs = Helpers.ExecuteCommand<SysObject>(spCmd, targetConn).ToDictionary(p => p.object_id);
            targetViews = Helpers.ExecuteCommand<SysObject>(vwCmd, targetConn).ToDictionary(p => p.object_id);
            MapTargetModels();
        }

        private void LoadDependencies()
        {
            var cmd = new SqlCommand(Query.GetDependencies());
            sourceDependencies = Helpers.ExecuteCommand<SysDependency>(cmd, sourceConn);
        }

        private void InitDependencies()
        {
            dbDependencies = sourceDependencies.GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.ToList().Select(x => x.depid).ToHashSet());
            isGenerated = sourceFuncs.Keys.Where(f => sourceTypes[f] == SQLTypes.InlineTableFunction.Description())
                .Union(sourceViews.Keys).Union(sourceProcs.Keys)
                .ToDictionary(id => id, id => false);
        }

        private void MapModels()
        {
            MapSourceModels();
            MapTargetModels();
        }

        private void MapSourceModels()
        {
            MapTables(sourceTables.Values, sourceObjects, sourceTypes);
            MapConstraints(sourceConstraints.Values, sourceObjects, sourceTypes);
            MapForeignKeys(sourceKeys.Values, sourceObjects, sourceTypes);
            MapSourceObjects(sourceFuncs.Values);
            MapSourceObjects(sourceProcs.Values);
            MapSourceObjects(sourceViews.Values);
            InitDependencies();
        }

        private void MapTargetModels()
        {
            MapTables(targetTables.Values, targetObjects, targetTypes);
            MapConstraints(targetConstraints.Values, targetObjects, targetTypes);
            MapForeignKeys(targetKeys.Values, targetObjects, targetTypes);
            MapTargetObjects(targetFuncs.Values);
            MapTargetObjects(targetProcs.Values);
            MapTargetObjects(targetViews.Values);
        }

        private void MapTables(IEnumerable<SysTable> tables, Dictionary<string, string> objectDict, Dictionary<string, string> typeDict)
        {
            foreach (var @object in tables)
            {
                objectDict[@object.qualified_name] = @object.object_id;
                typeDict[@object.object_id] = Enumerations.GetDescription(SQLTypes.UserTable);
            }
        }

        private void MapConstraints(IEnumerable<SysConstraint> constraints, Dictionary<string, string> objectDict, Dictionary<string, string> typeDict)
        {
            foreach (var @object in constraints)
            {
                objectDict[@object.qualified_name] = @object.object_id;
                typeDict[@object.object_id] = @object.type;
            }
        }

        private void MapForeignKeys(IEnumerable<SysForeignKey> keys, Dictionary<string, string> objectDict, Dictionary<string, string> typeDict)
        {
            foreach (var @object in keys)
            {
                objectDict[@object.qualified_name] = @object.constraint_id;
                typeDict[@object.constraint_id] = Enumerations.GetDescription(SQLTypes.ForeignKey);
            }
        }

        private void MapSourceObjects(IEnumerable<SysObject> objects)
        {
            MapObjects(objects, sourceObjects, sourceTypes);
        }

        private void MapTargetObjects(IEnumerable<SysObject> objects)
        {
            MapObjects(objects, targetObjects, targetTypes);
        }

        private void MapObjects(IEnumerable<SysObject> objects, Dictionary<string, string> objectDict, Dictionary<string, string> typeDict)
        {
            foreach (var @object in objects)
            {
                objectDict[@object.qualified_name] = @object.object_id;
                typeDict[@object.object_id] = @object.type;
            }
        }

        private bool ObjectChanged(SysObject @object, Dictionary<string, SysObject> targetSet)
        {
            if (!targetSet.ContainsKey(@object.qualified_name))
            {
                return false;
            }
            else
            {
                var targetObject = targetSet[@object.qualified_name];
                return targetObject.modify_date.HasValue && @object.modify_date > targetObject.modify_date;
            }
        }

        private void ReadModel(string pathToFile = null)
        {
            pathToFile ??= $"{path}\\model.json";
            var json = File.ReadAllText(pathToFile);
            DbModel dbModel = JsonSerializer.Deserialize<DbModel>(json);
            sourceDb = dbModel.Database.Name;
            sourceId = dbModel.Database.Id;
            sourceSchemas = (Dictionary<string, int>)dbModel.Schemas;
            sourceTables = (Dictionary<string, SysTable>)dbModel.Tables;
            sourceColumns = (Dictionary<string, List<SysColumn>>)dbModel.Columns;
            sourceKeys = (Dictionary<string, SysForeignKey>)dbModel.ForeignKeys;
            sourceConstraints = (Dictionary<string, SysConstraint>)dbModel.Constraints;
            sourceIndexes = (Dictionary<string, SysIndex>)dbModel.Indexes;
            sourceTableTypes = (Dictionary<string, SysTableType>)dbModel.TableTypes;
            sourceProcs = (Dictionary<string, SysObject>)dbModel.Procedures;
            sourceFuncs = (Dictionary<string, SysObject>)dbModel.Functions;
            sourceViews = (Dictionary<string, SysObject>)dbModel.Views;
            sourceDependencies = (List<SysDependency>)dbModel.Dependencies;
            MapSourceModels();
        }

        private void ReadData(string pathToFile = null)
        {
            pathToFile ??= $"{path}\\data.json";
            var json = File.ReadAllText(pathToFile);
            dbData = JsonSerializer.Deserialize<DbData>(json);
        }

        private void WriteModel()
        {
            DbModel dbModel = new DbModel();
            dbModel.Schemas = sourceSchemas;
            dbModel.Tables = sourceTables;
            dbModel.Columns = sourceColumns;
            dbModel.ForeignKeys = sourceKeys;
            dbModel.Constraints = sourceConstraints;
            dbModel.Indexes = sourceIndexes;
            dbModel.TableTypes = sourceTableTypes;
            dbModel.Functions = sourceFuncs;
            dbModel.Views = sourceViews;
            dbModel.Procedures = sourceProcs;
            dbModel.Dependencies = sourceDependencies;
            dbModel.Database = new DbModel.DbInfo()
            {
                Name = sourceDb,
                Id = sourceId
            };
            var json = JsonSerializer.Serialize(dbModel, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            File.WriteAllText($"{path}\\model.json", json);
        }

        private void WriteData()
        {
            var json = JsonSerializer.Serialize(dbData, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            File.WriteAllText($"{path}\\data.json", json);
        }
    }
}
