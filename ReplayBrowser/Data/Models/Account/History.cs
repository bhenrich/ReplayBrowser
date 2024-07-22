﻿namespace ReplayBrowser.Data.Models.Account;

public class HistoryEntry
{
    public int Id { get; set; }
    
    public string Action { get; set; }
    public DateTime Time { get; set; }
    public string? Details { get; set; }
}

public enum Action
{
    // Account actions
    AccountSettingsChanged,
    Login,
    
    // Site actions
    SearchPerformed,
    LeaderboardViewed,
    ProfileViewed,
    MainPageViewed,
}