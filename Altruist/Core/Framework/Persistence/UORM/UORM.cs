/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

namespace Altruist.UORM;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public class VaultAttribute : Attribute
{
    public string Name { get; }
    public string Keyspace { get; } = "altruist";
    public string DbToken { get; } = "ScyllaDB";
    public bool StoreHistory { get; }
    public VaultAttribute(string Name, bool StoreHistory = false, string Keyspace = "altruist", string DbToken = "ScyllaDB") => (this.Name, this.StoreHistory, this.Keyspace, this.DbToken) = (Name, StoreHistory, Keyspace, DbToken);
}

[AttributeUsage(AttributeTargets.Class)]
public class VaultPrimaryKeyAttribute : Attribute
{
    public string[] Keys { get; }
    public VaultPrimaryKeyAttribute(params string[] keys) => Keys = keys;
}

[AttributeUsage(AttributeTargets.Property)]
public class VaultColumnIndexAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Property)]
public class VaultIgnoredAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Class)]
public class VaultSortingByAttribute : Attribute
{
    public string Name { get; }
    public bool Ascending { get; }
    public VaultSortingByAttribute(
        string name,
        bool ascending = false) => (Name, Ascending) = (name, ascending);
}

[AttributeUsage(AttributeTargets.Property)]
public class VaultColumnAttribute : Attribute
{
    public string? Name { get; }
    public VaultColumnAttribute(string? name = null) => Name = name;
}
