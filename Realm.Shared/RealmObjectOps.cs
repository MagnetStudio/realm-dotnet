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

namespace Realms
{
    [Preserve(AllMembers = true)]
    internal static class RealmObjectOps
    {
        public static RealmList<T> GetList<T>(Realm realm, ObjectHandle handle, IntPtr propertyIndex, string objectType) where T : RealmObject
        {
            var listHandle = handle.TableLinkList(propertyIndex);
            return new RealmList<T>(realm, listHandle, realm.Metadata[objectType]);
        }

        public static T GetObject<T>(Realm realm, ObjectHandle handle, IntPtr propertyIndex, string objectType) where T : RealmObject
        {
            var linkedRowPtr = NativeTable.GetLink(handle, propertyIndex);
            if (linkedRowPtr == IntPtr.Zero)
            {
                return null;
            }

            return (T)realm.MakeObjectForRow(objectType, linkedRowPtr);
        }

        public static void SetObject(Realm realm, ObjectHandle handle, IntPtr propertyIndex, RealmObject @object)
        {
            if (@object == null)
            {
                NativeTable.ClearLink(handle, propertyIndex);
            }
            else
            {
                if (!@object.IsManaged)
                {
                    realm.Manage(@object);
                }

                NativeTable.SetLink(handle, propertyIndex, @object.ObjectHandle);
            }
        }
    }
}