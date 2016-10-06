////////////////////////////////////////////////////////////////////////////
//
// Copyright 2016 Realm Inc.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
#if __IOS__
using ObjCRuntime;
#endif
using Realms.Native;

namespace Realms
{
    /// <summary>
    /// A Realm instance (also referred to as a realm) represents a Realm database.
    /// </summary>
    /// <remarks>Warning: Realm instances are not thread safe and can not be shared across threads 
    /// You must call GetInstance on each thread in which you want to interact with the realm. 
    /// </remarks>
    public class Realm : IDisposable
    {
        #region static

        static Realm()
        {
            NativeCommon.Initialize();
            NativeCommon.register_notify_realm_changed(NotifyRealmChanged);
        }

#if __IOS__
        [MonoPInvokeCallback(typeof(NativeCommon.NotifyRealmCallback))]
#endif
        private static void NotifyRealmChanged(IntPtr realmHandle)
        {
            var gch = GCHandle.FromIntPtr(realmHandle);
            ((Realm)gch.Target).NotifyChanged(EventArgs.Empty);
        }

        /// <summary>
        /// Gets the <see cref="RealmConfiguration"/> that controls this realm's path and other settings.
        /// </summary>
        public RealmConfiguration Config { get; private set; }

        /// <summary>
        /// Factory for a Realm instance for this thread.
        /// </summary>
        /// <param name="databasePath">Path to the realm, must be a valid full path for the current platform, relative subdirectory, or just filename.</param>
        /// <remarks>If you specify a relative path, sandboxing by the OS may cause failure if you specify anything other than a subdirectory. <br />
        /// Instances are cached for a given absolute path and thread, so you may get back the same instance.
        /// </remarks>
        /// <returns>A realm instance, possibly from cache.</returns>
        /// <exception cref="RealmFileAccessErrorException">Throws error if the file system returns an error preventing file creation.</exception>
        public static Realm GetInstance(string databasePath)
        {
            var config = RealmConfiguration.DefaultConfiguration;
            if (!string.IsNullOrEmpty(databasePath))
            {
                config = config.ConfigWithPath(databasePath);
            }

            return GetInstance(config);
        }

        /// <summary>
        /// Factory for a Realm instance for this thread.
        /// </summary>
        /// <param name="config">Optional configuration.</param>
        /// <returns>A realm instance.</returns>
        /// <exception cref="RealmFileAccessErrorException">Throws error if the file system returns an error, preventing file creation.</exception>
        public static Realm GetInstance(RealmConfiguration config = null)
        {
            return GetInstance(config, null);
        }

        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.Success)]
        internal static Realm GetInstance(RealmConfiguration config, RealmSchema schema)
        {
            config = config ?? RealmConfiguration.DefaultConfiguration;

            var srHandle = new SharedRealmHandle();

            if (schema == null)
            {
                if (config.ObjectClasses != null)
                {
                    schema = RealmSchema.CreateSchemaForClasses(config.ObjectClasses);
                }
                else
                {
                    schema = RealmSchema.Default;
                }
            }

            var configuration = new Native.Configuration
            {
                Path = config.DatabasePath,
                read_only = config.ReadOnly,
                delete_if_migration_needed = config.ShouldDeleteIfMigrationNeeded,
                schema_version = config.SchemaVersion
            };

            Migration migration = null;
            if (config.MigrationCallback != null)
            {
                migration = new Migration(config, schema);
                migration.PopulateConfiguration(ref configuration);
            }

            var srPtr = IntPtr.Zero;
            try
            {
                srPtr = srHandle.Open(configuration, schema, config.EncryptionKey);
            }
            catch (ManagedExceptionDuringMigrationException)
            {
                throw new AggregateException("Exception occurred in a Realm migration callback. See inner exception for more details.", migration?.MigrationException);
            }

            RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
                /* Retain handle in a constrained execution region */
            }
            finally
            {
                srHandle.SetHandle(srPtr);
            }

            return new Realm(srHandle, config, schema);
        }

        #endregion

        internal readonly SharedRealmHandle SharedRealmHandle;
        internal readonly Dictionary<string, RealmObject.Metadata> Metadata;

        internal bool IsInTransaction => SharedRealmHandle.IsInTransaction();

        /// <summary>
        /// Gets the <see cref="RealmSchema"/> instance that describes all the types that can be stored in this <see cref="Realm"/>.
        /// </summary>
        public RealmSchema Schema { get; }

        internal Realm(SharedRealmHandle sharedRealmHandle, RealmConfiguration config, RealmSchema schema)
        {
            SharedRealmHandle = sharedRealmHandle;
            Config = config;

            Metadata = schema.ToDictionary(t => t.Name, CreateRealmObjectMetadata);
            Schema = schema;
        }

        private RealmObject.Metadata CreateRealmObjectMetadata(Schema.ObjectSchema schema)
        {
            var table = this.GetTable(schema);
            Weaving.IRealmObjectHelper helper;

            if (schema.Type != null && !Config.Dynamic)
            {
                var wovenAtt = schema.Type.GetCustomAttribute<WovenAttribute>();
                if (wovenAtt == null)
                {
                    throw new RealmException($"Fody not properly installed. {schema.Type.FullName} is a RealmObject but has not been woven.");
                }

                helper = (Weaving.IRealmObjectHelper)Activator.CreateInstance(wovenAtt.HelperType);
            }
            else
            {
                helper = Dynamic.DynamicRealmObjectHelper.Instance;
            }

            // build up column index in a loop so can spot and cache primary key index on the way
            var initColumnMap = new Dictionary<string, IntPtr>();
            var initPrimaryKeyIndex = -1;
            foreach (var prop in schema)
            {
                var colIndex = NativeTable.GetColumnIndex(table, prop.Name);
                initColumnMap.Add(prop.Name, colIndex);
                if (prop.IsPrimaryKey)
                {
                    initPrimaryKeyIndex = (int)colIndex;
                }
            }

            return new RealmObject.Metadata
            {
                Table = table,
                Helper = helper,
                ColumnIndices = initColumnMap,
                PrimaryKeyColumnIndex = initPrimaryKeyIndex,
                Schema = schema
            };
        }

        /// <summary>
        /// Handler type used by <see cref="RealmChanged"/> 
        /// </summary>
        /// <param name="sender">The Realm which has changed.</param>
        /// <param name="e">Currently an empty argument, in future may indicate more details about the change.</param>
        public delegate void RealmChangedEventHandler(object sender, EventArgs e);

        private event RealmChangedEventHandler _realmChanged;

        /// <summary>
        /// Triggered when a realm has changed (i.e. a transaction was committed).
        /// </summary>
        public event RealmChangedEventHandler RealmChanged
        {
            add
            {
                if (_realmChanged == null)
                {
                    var managedRealmHandle = GCHandle.Alloc(this, GCHandleType.Weak);
                    SharedRealmHandle.BindToManagedRealmHandle(GCHandle.ToIntPtr(managedRealmHandle));
                }

                _realmChanged += value;
            }

            remove
            {
                _realmChanged -= value;
            }
        }

        private void NotifyChanged(EventArgs e)
        {
            _realmChanged?.Invoke(this, e);
        }

        /// <summary>
        /// Checks if database has been closed.
        /// </summary>
        /// <returns>True if closed.</returns>
        public bool IsClosed => SharedRealmHandle.IsClosed;

        /// <summary>
        /// Closes the Realm if not already closed. Safe to call repeatedly.
        /// Note that this will close the file. Other references to the same database
        /// on the same thread will be invalidated.
        /// </summary>
        public void Close()
        {
            if (IsClosed)
            {
                return;
            }

            Dispose();
        }

        ~Realm()
        {
            Dispose(false);
        }

        /// <summary>
        ///  Dispose automatically closes the Realm if not already closed.
        /// </summary>
        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Dispose(true);
        }

        private void Dispose(bool disposing)
        {
            if (IsClosed)
            {
                throw new ObjectDisposedException(nameof(Realm));
            }

            if (disposing && !(SharedRealmHandle is UnownedRealmHandle))
            {
                SharedRealmHandle.CloseRealm();
            }

            SharedRealmHandle.Close();  // Note: this closes the *handle*, it does not trigger realm::Realm::close().
        }

        /// <summary>
        /// Generic override determines whether the specified <see cref="System.Object"/> is equal to the current Realm.
        /// </summary>
        /// <param name="obj">The <see cref="System.Object"/> to compare with the current Realm.</param>
        /// <returns><c>true</c> if the Realms are functionally equal.</returns>
        public override bool Equals(object obj) => Equals(obj as Realm);

        /// <summary>
        /// Determines whether the specified Realm is equal to the current Realm.
        /// </summary>
        /// <param name="other">The Realm to compare with the current Realm.</param>
        /// <returns><c>true</c> if the Realms are functionally equal.</returns>
        public bool Equals(Realm other)
        {
            if (other == null)
            {
                return false;
            }

            if (ReferenceEquals(this, other))
            {
                return true;
            }

            return Config.Equals(other.Config) && IsClosed == other.IsClosed;
        }

        /// <summary>
        /// Determines whether this instance is the same core instance as the passed in argument.
        /// </summary>
        /// <remarks>
        /// You can, and should, have multiple instances open on different threads which have the same path and open the same Realm.
        /// </remarks>
        /// <returns><c>true</c> if this instance is the same core instance; otherwise, <c>false</c>.</returns>
        /// <param name="other">The Realm to compare with the current Realm.</param>
        public bool IsSameInstance(Realm other)
        {
            return SharedRealmHandle.IsSameInstance(other.SharedRealmHandle);
        }

        /// <summary>
        /// Serves as a hash function for a Realm based on the core instance.
        /// </summary>
        /// <returns>A hash code for this instance that is suitable for use in hashing algorithms and data structures such as a
        /// hash table.</returns>
        public override int GetHashCode()
        {
            return (int)SharedRealmHandle.DangerousGetHandle();
        }

        /// <summary>
        ///  Deletes all the files associated with a realm. Hides knowledge of the auxiliary filenames from the programmer.
        /// </summary>
        /// <param name="configuration">A configuration which supplies the realm path.</param>
        public static void DeleteRealm(RealmConfiguration configuration)
        {
            // TODO add cache checking when implemented, https://github.com/realm/realm-dotnet/issues/308
            // when cache checking, uncomment in IntegrationTests.cs RealmInstanceTests.DeleteRealmFailsIfOpenSameThread and add a variant to test open on different thread
            var lockOnWhileDeleting = new object();
            lock (lockOnWhileDeleting)
            {
                var fullpath = configuration.DatabasePath;
                File.Delete(fullpath);
                File.Delete(fullpath + ".log_a");  // eg: name at end of path is EnterTheMagic.realm.log_a   
                File.Delete(fullpath + ".log_b");
                File.Delete(fullpath + ".log");
                File.Delete(fullpath + ".lock");
                File.Delete(fullpath + ".note");
            }
        }

        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.Success)]
        private TableHandle GetTable(Schema.ObjectSchema schema)
        {
            var result = new TableHandle();
            var tableName = schema.Name;

            RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
                /* Retain handle in a constrained execution region */
            }
            finally
            {
                var tablePtr = SharedRealmHandle.GetTable(tableName);
                result.SetHandle(tablePtr);
            }

            return result;
        }

        /// <summary>
        /// Factory for a managed object in a realm. Only valid within a Write transaction.
        /// </summary>
        /// <remarks>Using CreateObject is more efficient than creating standalone objects, assigning their values, then using Manage because it avoids copying properties to the realm.</remarks>
        /// <typeparam name="T">The Type T must be a RealmObject.</typeparam>
        /// <returns>An object which is already managed.</returns>
        /// <exception cref="RealmOutsideTransactionException">If you invoke this when there is no write Transaction active on the realm.</exception>
        public T CreateObject<T>() where T : RealmObject, new()
        {
            RealmObject.Metadata metadata;
            var ret = CreateObject(typeof(T).Name, out metadata);
            if (typeof(T) != metadata.Schema.Type)
            {
                throw new ArgumentException($"The type {typeof(T).FullName} does not match the original type the schema was created for - {metadata.Schema.Type?.FullName}");
            }

            return (T)ret;
        }

        /// <summary>
        /// Factory for a managed object in a realm. Only valid within a Write transaction.
        /// </summary>
        /// <returns>A dynamically-accessed Realm object.</returns>
        /// <param name="className">The type of object to create as defined in the schema.</param>
        /// <remarks>
        /// If the realm instance has been created from an un-typed schema (such as when migrating from an older version of a realm) the returned object will be purely dynamic.
        /// If the realm has been created from a typed schema as is the default case when calling <code>Realm.GetInstance()</code> the returned object will be an instance of a user-defined class, as if created by <code>Realm.CreateObject&lt;T&gt;()</code>.
        /// </remarks>
        public dynamic CreateObject(string className)
        {
            RealmObject.Metadata ignored;
            return CreateObject(className, out ignored);
        }

        private RealmObject CreateObject(string className, out RealmObject.Metadata metadata)
        {
            if (!IsInTransaction)
            {
                throw new RealmOutsideTransactionException("Cannot create Realm object outside write transactions");
            }

            if (!Metadata.TryGetValue(className, out metadata))
            {
                throw new ArgumentException($"The class {className} is not in the limited set of classes for this realm");
            }

            var result = metadata.Helper.CreateInstance();

            var rowPtr = NativeTable.AddEmptyRow(metadata.Table);
            var rowHandle = CreateRowHandle(rowPtr, SharedRealmHandle);

            result._Manage(this, rowHandle, metadata);
            return result;
        }

        internal RealmObject MakeObjectForRow(RealmObject.Metadata metadata, IntPtr rowPtr)
        {
            return MakeObjectForRow(metadata, CreateRowHandle(rowPtr, SharedRealmHandle));
        }

        internal RealmObject MakeObjectForRow(string className, IntPtr rowPtr)
        {
            return MakeObjectForRow(Metadata[className], CreateRowHandle(rowPtr, SharedRealmHandle));
        }

        internal RealmObject MakeObjectForRow(string className, RowHandle row)
        {
            return MakeObjectForRow(Metadata[className], row);
        }

        internal RealmObject MakeObjectForRow(RealmObject.Metadata metadata, RowHandle row)
        {
            var ret = metadata.Helper.CreateInstance();
            ret._Manage(this, row, metadata);
            return ret;
        }

        internal ResultsHandle MakeResultsForTable(RealmObject.Metadata metadata)
        {
            var resultsPtr = NativeTable.CreateResults(metadata.Table, SharedRealmHandle);
            return CreateResultsHandle(resultsPtr);
        }

        internal ResultsHandle MakeResultsForQuery(QueryHandle builtQuery, SortDescriptorBuilder optionalSortDescriptorBuilder)
        {
            var resultsPtr = IntPtr.Zero;
            if (optionalSortDescriptorBuilder == null)
            {
                resultsPtr = builtQuery.CreateResults(SharedRealmHandle);
            }
            else
            {
                resultsPtr = builtQuery.CreateSortedResults(SharedRealmHandle, optionalSortDescriptorBuilder);
            }

            return CreateResultsHandle(resultsPtr);
        }

        internal SortDescriptorBuilder CreateSortDescriptorForTable(RealmObject.Metadata metadata)
        {
            return new SortDescriptorBuilder(metadata);
        }

        /// <summary>
        /// This realm will start managing a RealmObject which has been created as a standalone object.
        /// </summary>
        /// <typeparam name="T">The Type T must not only be a RealmObject but also have been processed by the Fody weaver, so it has persistent properties.</typeparam>
        /// <param name="obj">Must be a standalone object, null not allowed.</param>
        /// <exception cref="RealmOutsideTransactionException">If you invoke this when there is no write Transaction active on the realm.</exception>
        /// <exception cref="RealmObjectAlreadyManagedByRealmException">You can't manage the same object twice. This exception is thrown, rather than silently detecting the mistake, to help you debug your code</exception>
        /// <exception cref="RealmObjectManagedByAnotherRealmException">You can't manage an object with more than one realm</exception>
        public void Manage<T>(T obj) where T : RealmObject
        {
            if (obj == null)
            {
                throw new ArgumentNullException(nameof(obj));
            }

            if (obj.IsManaged)
            {
                if (obj.Realm.SharedRealmHandle == this.SharedRealmHandle)
                {
                    throw new RealmObjectAlreadyManagedByRealmException("The object is already managed by this realm");
                }

                throw new RealmObjectManagedByAnotherRealmException("Cannot start to manage an object with a realm when it's already managed by another realm");
            }

            if (!IsInTransaction)
            {
                throw new RealmOutsideTransactionException("Cannot start managing a Realm object outside write transactions");
            }

            var metadata = Metadata[typeof(T).Name];
            var tableHandle = metadata.Table;

            var rowPtr = NativeTable.AddEmptyRow(tableHandle);
            var rowHandle = CreateRowHandle(rowPtr, SharedRealmHandle);

            obj._Manage(this, rowHandle, metadata);
            obj._CopyDataFromBackingFieldsToRow();
        }

        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.Success)]
        internal static ResultsHandle CreateResultsHandle(IntPtr resultsPtr)
        {
            var resultsHandle = new ResultsHandle();

            RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
                /* Retain handle in a constrained execution region */
            }
            finally
            {
                resultsHandle.SetHandle(resultsPtr);
            }

            return resultsHandle;
        }

        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.Success)]
        internal static RowHandle CreateRowHandle(IntPtr rowPtr, SharedRealmHandle sharedRealmHandle)
        {
            var rowHandle = new RowHandle(sharedRealmHandle);

            RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
                /* Retain handle in a constrained execution region */
            }
            finally
            {
                rowHandle.SetHandle(rowPtr);
            }

            return rowHandle;
        }

        /// <summary>
        /// Factory for a write Transaction. Essential object to create scope for updates.
        /// </summary>
        /// <example><c>
        /// using (var trans = realm.BeginWrite()) 
        /// { 
        ///     var rex = realm.CreateObject&lt;Dog&gt;();
        ///     rex.Name = "Rex";
        ///     trans.Commit();
        /// }</c>
        /// </example>
        /// <returns>A transaction in write mode, which is required for any creation or modification of objects persisted in a Realm.</returns>
        public Transaction BeginWrite()
        {
            return new Transaction(this);
        }

        /// <summary>
        /// Execute an action inside a temporary transaction. If no exception is thrown, the transaction will automatically
        /// be committed.
        /// </summary>
        /// <remarks>
        /// Creates its own temporary transaction and commits it after running the lambda passed to `action`. 
        /// Be careful of wrapping multiple single property updates in multiple `Write` calls. It is more efficient to update several properties 
        /// or even create multiple objects in a single Write, unless you need to guarantee finer-grained updates.
        /// </remarks>
        /// <example><c>
        /// realm.Write(() => 
        /// {
        ///     d = realm.CreateObject&lt;Dog&gt;();
        ///     d.Name = "Eddie";
        ///     d.Age = 5;
        /// });</c>
        /// </example>
        /// <param name="action">Action to perform inside a transaction, creating, updating or removing objects.</param>
        public void Write(Action action)
        {
            using (var transaction = BeginWrite())
            {
                action();
                transaction.Commit();
            }
        }

        /// <summary>
        /// Execute an action inside a temporary transaction on a worker thread. If no exception is thrown, the transaction will automatically
        /// be committed.
        /// </summary>
        /// <remarks>
        /// Opens a new instance of this realm on a worker thread and executes <c>action</c> inside a write transaction.
        /// Realms and realm objects are thread-affine, so capturing any such objects in the <c>action</c> delegate will lead to errors
        /// if they're used on the worker thread.
        /// </remarks>
        /// <example>
        /// await realm.WriteAsync(tempRealm =&gt; 
        /// {
        ///     var pongo = tempRealm.GetAll&lt;Dog&gt;().Single(d =&gt; d.Name == "Pongo");
        ///     var missis = tempRealm.GetAll&lt;Dog&gt;().Single(d =&gt; d.Name == "Missis");
        ///     for (var i = 0; i &lt; 15; i++)
        ///     {
        ///         var pup = tempRealm.CreateObject&lt;Dog&gt;();
        ///         pup.Breed = "Dalmatian";
        ///         pup.Mum = missis;
        ///         pup.Dad = pongo;
        ///     }
        /// });
        /// </example>
        /// <param name="action">Action to perform inside a transaction, creating, updating or removing objects.</param>
        /// <returns>A standard <c>Task</c> so it can be used by <c>await</c>.</returns>
        public Task WriteAsync(Action<Realm> action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            // avoid capturing `this` in the lambda
            var configuration = Config;
            return Task.Run(() =>
            {
                using (var realm = GetInstance(configuration))
                using (var transaction = realm.BeginWrite())
                {
                    action(realm);
                    transaction.Commit();
                }
            });
        }

        /// <summary>
        /// Update a Realm and outstanding objects to point to the most recent data for this Realm.
        /// This is only necessary when you have a Realm on a thread without a runloop that needs manual refreshing.
        /// </summary>
        /// <returns>
        /// Whether the realm had any updates. Note that this may return true even if no data has actually changed.
        /// </returns>
        public bool Refresh()
        {
            return SharedRealmHandle.Refresh();
        }

        /// <summary>
        /// Extract an iterable set of objects for direct use or further query.
        /// </summary>
        /// <typeparam name="T">The Type T must be a RealmObject.</typeparam>
        /// <returns>A RealmResults that without further filtering, allows iterating all objects of class T, in this realm.</returns>
        public RealmResults<T> GetAll<T>() where T: RealmObject
        {
            var type = typeof(T);
            RealmObject.Metadata metadata;
            if (!Metadata.TryGetValue(type.Name, out metadata) || metadata.Schema.Type != type)
            {
                throw new ArgumentException($"The class {type.Name} is not in the limited set of classes for this realm");
            }

            return new RealmResults<T>(this, metadata, true);
        }

        /// <summary>
        /// Get a view of all the objects of a particular type.
        /// </summary>
        /// <param name="className">The type of the objects as defined in the schema.</param>
        /// <remarks>Because the objects inside the view are accessed dynamically, the view cannot be queried into using LINQ or other expression predicates.</remarks>
        /// <returns>A RealmResults that without further filtering, allows iterating all objects of className, in this realm.</returns>
        public RealmResults<dynamic> GetAll(string className)
        {
            RealmObject.Metadata metadata;
            if (!Metadata.TryGetValue(className, out metadata))
            {
                throw new ArgumentException($"The class {className} is not in the limited set of classes for this realm");
            }

            return new RealmResults<dynamic>(this, metadata, true);
        }


        #region Quick Find using primary key

        /// <summary>
        /// Fast lookup of an object from a class which has a PrimaryKey property.
        /// </summary>
        /// <typeparam name="T">The Type T must be a RealmObject.</typeparam>
        /// <param name="id">Id to be matched exactly, same as an == search. An argument of type <c>long</c> works for all integer properties, supported as PrimaryKey.</param>
        /// <returns>Null or an object matching the id.</returns>
        /// <exception cref="RealmClassLacksPrimaryKeyException">If the RealmObject class T lacks an [PrimaryKey].</exception>
        public T Find<T>(Int64 id) where T : RealmObject
        {
            var metadata = Metadata[typeof(T).Name];
            var rowPtr = NativeTable.RowForPrimaryKey(metadata.Table, metadata.PrimaryKeyColumnIndex, id);
            if (rowPtr == IntPtr.Zero)
            {
                return null;
            }

            return (T)MakeObjectForRow(metadata, rowPtr);
        }

        /// <summary>
        /// Fast lookup of an object from a class which has a PrimaryKey property.
        /// </summary>
        /// <typeparam name="T">The Type T must be a RealmObject.</typeparam>
        /// <param name="id">Id to be matched exactly, same as an == search.</param>
        /// <returns>Null or an object matching the id.</returns>
        /// <exception cref="RealmClassLacksPrimaryKeyException">If the RealmObject class T lacks an [PrimaryKey].</exception>
        public T Find<T>(string id) where T : RealmObject
        {
            var metadata = Metadata[typeof(T).Name];
            var rowPtr = NativeTable.RowForPrimaryKey(metadata.Table, metadata.PrimaryKeyColumnIndex, id);
            if (rowPtr == IntPtr.Zero)
            {
                return null;
            }

            return (T)MakeObjectForRow(metadata, rowPtr);
        }

        /// <summary>
        /// Fast lookup of an object for dynamic use, from a class which has a PrimaryKey property.
        /// </summary>
        /// <param name="className">Name of class in dynamic situation.</param>
        /// <param name="id">Id to be matched exactly, same as an == search.</param>
        /// <returns>Null or an object matching the id.</returns>
        /// <exception cref="RealmClassLacksPrimaryKeyException">If the RealmObject class lacks an [PrimaryKey].</exception>
        public RealmObject Find(string className, Int64 id)
        {
            var metadata = Metadata[className];
            var rowPtr = NativeTable.RowForPrimaryKey(metadata.Table, metadata.PrimaryKeyColumnIndex, id);
            if (rowPtr == IntPtr.Zero)
            {
                return null;
            }

            return MakeObjectForRow(metadata, rowPtr);
        }

        /// <summary>
        /// Fast lookup of an object for dynamic use, from a class which has a PrimaryKey property.
        /// </summary>
        /// <param name="className">Name of class in dynamic situation.</param>
        /// <param name="id">Id to be matched exactly, same as an == search.</param>
        /// <returns>Null or an object matching the id.</returns>
        /// <exception cref="RealmClassLacksPrimaryKeyException">If the RealmObject class lacks an [PrimaryKey].</exception>
        public RealmObject Find(string className, string id)
        {
            var metadata = Metadata[className];
            var rowPtr = NativeTable.RowForPrimaryKey(metadata.Table, metadata.PrimaryKeyColumnIndex, id);
            if (rowPtr == IntPtr.Zero)
            {
                return null;
            }

            return MakeObjectForRow(metadata, rowPtr);
        }

        #endregion Quick Find using primary key

        /// <summary>
        /// Removes a persistent object from this realm, effectively deleting it.
        /// </summary>
        /// <param name="obj">Must be an object persisted in this realm.</param>
        /// <exception cref="RealmOutsideTransactionException">If you invoke this when there is no write Transaction active on the realm.</exception>
        /// <exception cref="System.ArgumentNullException">If you invoke this with a standalone object.</exception>
        public void Remove(RealmObject obj)
        {
            if (!IsInTransaction)
            {
                throw new RealmOutsideTransactionException("Cannot remove Realm object outside write transactions");
            }

            var tableHandle = obj.ObjectMetadata.Table;
            NativeTable.RemoveRow(tableHandle, obj.RowHandle);
        }

        /// <summary>
        /// Remove objects matching a query from the realm.
        /// </summary>
        /// <typeparam name="T">Type of the objects to remove.</typeparam>
        /// <param name="range">The query to match for.</param>
        public void RemoveRange<T>(RealmResults<T> range)
        {
            if (range == null)
            {
                throw new ArgumentNullException(nameof(range));
            }

            if (!IsInTransaction)
            {
                throw new RealmOutsideTransactionException("Cannot remove Realm objects outside write transactions");
            }

            range.ResultsHandle.Clear();
        }

        /// <summary>
        /// Remove all objects of a type from the realm.
        /// </summary>
        /// <typeparam name="T">Type of the objects to remove.</typeparam>
        public void RemoveAll<T>() where T : RealmObject
        {
            RemoveRange(GetAll<T>());
        }

        /// <summary>
        /// Remove all objects of a type from the realm.
        /// </summary>
        /// <param name="className">Type of the objects to remove as defined in the schema.</param>
        public void RemoveAll(string className)
        {
            RemoveRange(GetAll(className));
        }

        /// <summary>
        /// Remove all objects of all types managed by this realm.
        /// </summary>
        public void RemoveAll()
        {
            if (!IsInTransaction)
            {
                throw new RealmOutsideTransactionException("Cannot remove all Realm objects outside write transactions");
            }

            foreach (var metadata in Metadata.Values)
            {
                var resultsHandle = MakeResultsForTable(metadata);
                resultsHandle.Clear();
            }
        }

        #region Obsolete methods

        [Obsolete("This method has been renamed. Use GetAll for the same results.")]
        /// <summary>
        /// Extract an iterable set of objects for direct use or further query.
        /// </summary>
        /// <typeparam name="T">The Type T must be a RealmObject.</typeparam>
        /// <returns>A RealmResults that without further filtering, allows iterating all objects of class T, in this realm.</returns>
        public RealmResults<T> All<T>() where T : RealmObject
        {
            return GetAll<T>();
        }

        [Obsolete("This method has been renamed. Use GetAll for the same results.")]
        /// <summary>
        /// Get a view of all the objects of a particular type
        /// </summary>
        /// <param name="className">The type of the objects as defined in the schema.</param>
        /// <remarks>Because the objects inside the view are accessed dynamically, the view cannot be queried into using LINQ or other expression predicates.</remarks>
        /// <returns>A RealmResults that without further filtering, allows iterating all objects of className, in this realm.</returns>
        public RealmResults<dynamic> All(string className)
        {
            return GetAll(className);
        }

        [Obsolete("This method has been renamed. Use Find for the same results.")]
        /// <summary>
        /// Fast lookup of an object from a class which has a PrimaryKey property.
        /// </summary>
        /// <typeparam name="T">The Type T must be a RealmObject.</typeparam>
        /// <param name="id">Id to be matched exactly, same as an == search. Int64 argument works for all integer properties supported as PrimaryKey.</param>
        /// <returns>Null or an object matching the id.</returns>
        /// <exception cref="RealmClassLacksPrimaryKeyException">If the RealmObject class T lacks an [PrimaryKey].</exception>
        public T ObjectForPrimaryKey<T>(Int64 id) where T : RealmObject
        {
            return Find<T>(id);
        }


        [Obsolete("This method has been renamed. Use Find for the same results.")]
        /// <summary>
        /// Fast lookup of an object from a class which has a PrimaryKey property.
        /// </summary>
        /// <typeparam name="T">The Type T must be a RealmObject.</typeparam>
        /// <param name="id">Id to be matched exactly, same as an == search.</param>
        /// <returns>Null or an object matdhing the id.</returns>
        /// <exception cref="RealmClassLacksPrimaryKeyException">If the RealmObject class T lacks an [PrimaryKey].</exception>
        public T ObjectForPrimaryKey<T>(string id) where T : RealmObject
        {
            return Find<T>(id);
        }


        [Obsolete("This method has been renamed. Use Find for the same results.")]
        /// <summary>
        /// Fast lookup of an object for dynamic use, from a class which has a PrimaryKey property.
        /// </summary>
        /// <param name="className">Name of class in dynamic situation.</param>
        /// <param name="id">Id to be matched exactly, same as an == search.</param>
        /// <returns>Null or an object matdhing the id.</returns>
        /// <exception cref="RealmClassLacksPrimaryKeyException">If the RealmObject class lacks an [PrimaryKey].</exception>
        public RealmObject ObjectForPrimaryKey(string className, Int64 id)
        {
            return Find(className, id);
        }


        [Obsolete("This method has been renamed. Use Find for the same results.")]
        /// <summary>
        /// Fast lookup of an object for dynamic use, from a class which has a PrimaryKey property.
        /// </summary>
        /// <param name="className">Name of class in dynamic situation.</param>
        /// <param name="id">Id to be matched exactly, same as an == search.</param>
        /// <returns>Null or an object matdhing the id.</returns>
        /// <exception cref="RealmClassLacksPrimaryKeyException">If the RealmObject class lacks an [PrimaryKey].</exception>
        public RealmObject ObjectForPrimaryKey(string className, string id)
        {
            return Find(className, id);
        }

        #endregion
    }
}