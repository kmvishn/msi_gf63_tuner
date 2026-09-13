// This file is part of YAMDCC (Yet Another MSI Dragon Center Clone).
// Copyright © Sparronator9999 and Contributors 2025.
//
// YAMDCC is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option)
// any later version.
//
// YAMDCC is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY
// or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for
// more details.
//
// You should have received a copy of the GNU General Public License along with
// YAMDCC. If not, see <https://www.gnu.org/licenses/>.

using Newtonsoft.Json;
using System;

namespace YAMDCC.Updater.CodebergApi;

// suppress warning about default values never getting overwritten
// since they get populated when deserialising JSON to these classes
#pragma warning disable CS0649
internal sealed class Author
{
    [JsonProperty("id")]
    public int Id;

    [JsonProperty("login")]
    public string Login;

    [JsonProperty("login_name")]
    public string LoginName;

    [JsonProperty("source_id")]
    public int SourceId;

    [JsonProperty("full_name")]
    public string FullName;

    [JsonProperty("email")]
    public string Email;

    [JsonProperty("avatar_url")]
    public string AvatarUrl;

    [JsonProperty("html_url")]
    public string HtmlUrl;

    [JsonProperty("language")]
    public string Language;

    [JsonProperty("is_admin")]
    public bool IsAdmin;

    [JsonProperty("last_login")]
    public DateTimeOffset LastLogin;

    [JsonProperty("created")]
    public DateTimeOffset Created;

    [JsonProperty("restricted")]
    public bool Restricted;

    [JsonProperty("active")]
    public bool Active;

    [JsonProperty("prohibit_login")]
    public bool ProhibitLogin;

    [JsonProperty("location")]
    public string Location;

    [JsonProperty("pronouns")]
    public string Pronouns;

    [JsonProperty("website")]
    public string Website;

    [JsonProperty("description")]
    public string Description;

    [JsonProperty("visibility")]
    public string Visibility;

    [JsonProperty("followers_count")]
    public int Followers;

    [JsonProperty("following_count")]
    public int Following;

    [JsonProperty("starred_repos_count")]
    public int StarredRepos;

    [JsonProperty("username")]
    public string Username;

}
#pragma warning restore CS0649
