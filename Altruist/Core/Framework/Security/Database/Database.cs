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

using Altruist;
using Altruist.Security;

/// <summary>A vault of accounts that can also check a login token. Altruist ships no implementation.</summary>
public interface ILoginVault : IVault<AccountModel>
{
    /// <summary>True when <paramref name="token"/> matches a stored account.</summary>
    /// <typeparam name="TLoginToken">The credential shape (e.g. <see cref="UsernamePasswordLoginRequest"/>).</typeparam>
    bool Login<TLoginToken>(TLoginToken token) where TLoginToken : ILoginToken;
}
