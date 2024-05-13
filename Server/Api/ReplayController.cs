﻿using Humanizer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Caching.Memory;
using Serilog;
using Server.Helpers;
using Server.Metrics;
using Shared.Models;
using Shared.Models.Account;
using Action = Shared.Models.Account.Action;

namespace Server.Api;

[ApiController]
[Route("api/[controller]")]
public class ReplayController : ControllerBase
{
    private readonly ReplayDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly ReplayMetrics _metrics;

    public ReplayController(ReplayDbContext context, IMemoryCache cache, ReplayMetrics metrics)
    {
        _context = context;
        _cache = cache;
        _metrics = metrics;
    }

    [HttpPost]
    public async Task<ActionResult> UploadReplay(IFormFile file)
    {
        if (!HttpContext.Request.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Due to security reasons, you can only upload replays from localhost. If you want to add a replay source, please contact the server administrator.");
        }
        
        var stream = file.OpenReadStream();
        var replay = ReplayParser.ReplayParser.ParseReplay(stream);
        stream.Close();
        
        _context.Replays.Add(replay);
        await _context.SaveChangesAsync();

        return Ok();
    }
    
    
    /// <summary>
    /// Returns a list of replay IDs that match the search query.
    /// </summary>
    /// <param name="mode">The search mode.</param>
    /// <param name="query">The search query.</param>
    [HttpGet]
    [Route("/search")]
    [Authorize(Policy = "TokenBased")]
    public async Task<ActionResult> SearchReplays(
        [FromQuery] string mode,
        [FromQuery] string query,
        [FromHeader] Guid? accountGuid,
        [FromQuery] int page = 0
        )
    {
        var searchMode = SearchMode.Gamemode;
        if (!Enum.TryParse<SearchMode>(mode, true, out var modeEnum))
        {
            // try to parse as a number
            if (int.TryParse(mode, out var modeInt))
            {
                searchMode = (SearchMode) modeInt;
            }
            else
            {
                // LAST TRY: try to parse it as a humanized string
                var humanized = mode.Humanize();
                // Use loop to find the enum value
                var didFind = false;
                foreach (SearchMode value in Enum.GetValues(typeof(SearchMode)))
                {
                    if (value.Humanize().Equals(humanized, StringComparison.OrdinalIgnoreCase))
                    {
                        searchMode = value;
                        didFind = true;
                        break;
                    }
                }

                if (!didFind)
                {
                    // If we still can't find it, return an error
                    return BadRequest($"The search mode '{mode}' is not valid. Valid search modes are: {string.Join(", ", Enum.GetNames(typeof(SearchMode)))}");
                }
            }
        }
        else
        {
            searchMode = modeEnum;
        }
        
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest("The search query cannot be empty.");
        }
        
        if (page < 0)
        {
            return BadRequest("The page number cannot be negative.");
        }

        var requester = await _context.Accounts
            .FirstOrDefaultAsync(a => a.Guid == accountGuid);
        
        requester?.History.Add(new HistoryEntry()
        {
            Action = Enum.GetName(typeof(Action), Action.SearchPerformed) ?? "Unknown",
            Time = DateTime.UtcNow,
            Details = $"Mode: {searchMode}, Query: {query}"
        });
        
        if (requester == null)
        {
            var systemAccount = await _context.Accounts
                .FirstOrDefaultAsync(a => a.Guid == Guid.Empty);
            
            systemAccount!.History.Add(new HistoryEntry() {
                Action = Enum.GetName(typeof(Action), Action.SearchPerformed) ?? "Unknown",
                Time = DateTime.UtcNow,
                Details = $"Mode: {searchMode}, Query: {query}"
            });
        }
        
        await _context.SaveChangesAsync();
        
        switch (searchMode)
        {
            case SearchMode.Guid:
                var foundGuidAccount = _context.Accounts
                    .Include(a => a.Settings)
                    .FirstOrDefault(a => a.Guid.ToString().ToLower().Contains(query.ToLower()));

                if (foundGuidAccount != null && foundGuidAccount.Settings.RedactInformation)
                {
                    // if the requestor is not the found account and the requestor is not an admin, deny access
                    if (requester == null || !requester.IsAdmin)
                    {
                        return Unauthorized("The account you are trying to search for is private. Contact the account owner and ask them to make their account public.");
                    }
                }
                break;
            
            case SearchMode.PlayerOocName:
                var foundOocAccount = _context.Accounts
                    .Include(a => a.Settings)
                    .FirstOrDefault(a => a.Username.ToLower().Contains(query.ToLower()));

                if (foundOocAccount != null && foundOocAccount.Settings.RedactInformation)
                {
                    // if the requestor is not the found account and the requestor is not an admin, deny access
                    if (requester == null || !requester.IsAdmin)
                    {
                        return Unauthorized("The account you are trying to search for is private. Contact the account owner and ask them to make their account public.");
                    }
                }
                break;
        }

        var found = SearchReplays(searchMode, query, page, Constants.ReplaysPerPage);
        
        var pageCount = Paginator.GetPageCount(found.Item2, Constants.ReplaysPerPage);
        
        var replays = FilterReplays(found.Item1, accountGuid);
        
        return Ok(new SearchResult()
        {
            Replays = replays,
            PageCount = pageCount,
            CurrentPage = page,
            TotalReplays = found.Item2,
            IsCache = found.Item3,
            SearchMode = searchMode,
            Query = query
        });
    }
    
    [HttpGet]
    [Route("/replays")]
    public async Task<ActionResult> GetAllReplays()
    {
        var replays = await _context.Replays.ToListAsync();
        var ids = replays.Select(x => x.Id);
        return Ok(ids);
    }
    
    /// <summary>
    /// Returns the most recent replays. Not sorted by date. Just the most recent replays stored in the database.
    /// </summary>
    [HttpGet]
    [Route("/replays/most-recent")]
    [Authorize(Policy = "TokenBased")]
    public async Task<ActionResult> GetMostRecentReplay(
        [FromHeader] Guid? accountGuid
        )
    {
        var replays = await _context.Replays
            .OrderByDescending(r => r.Id)
            .Include(r => r.RoundEndPlayers)
            .Take(32)
            .ToListAsync();
        
        replays = FilterReplays(replays, accountGuid);
        
        // Log the request
        var requester = _context.Accounts
            .FirstOrDefault(a => a.Guid == accountGuid);
        
        requester?.History.Add(new HistoryEntry() {
            Action = Enum.GetName(typeof(Action), Action.MainPageViewed) ?? "Unknown",
            Time = DateTime.UtcNow
        });
        
        if (requester == null)
        {
            var systemAccount = await _context.Accounts
                .FirstOrDefaultAsync(a => a.Guid == Guid.Empty);
            
            systemAccount!.History.Add(new HistoryEntry() {
                Action = Enum.GetName(typeof(Action), Action.MainPageViewed) ?? "Unknown",
                Time = DateTime.UtcNow
            });
        }
        
        await _context.SaveChangesAsync();
        
        return Ok(replays);
    }
    
    [HttpGet]
    [Route("/replay/{id}")]
    [Authorize(Policy = "TokenBased")]
    public async Task<ActionResult> GetReplay(
        int id,
        [FromHeader] Guid? accountGuid
        )
    {
        var replay = await _context.Replays
            .Include(r => r.RoundEndPlayers)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (replay == null)
        {
            return NotFound();
        }

        return Ok(FilterReplay(replay, accountGuid));
    }
    
    /// <summary>
    /// Searches a list of replays for a specific query.
    /// </summary>
    /// <param name="mode">The search mode.</param>
    /// <param name="query">The search query.</param>
    /// <param name="replays">The list of replays to search.</param>
    /// <returns>
    /// A list of replays that match the search query.
    /// </returns>
    /// <exception cref="NotImplementedException">
    /// Thrown when the search mode is not implemented.
    /// </exception>
    private (List<Replay>, int, bool) SearchReplays(SearchMode mode, string query, int page, int pageSize)
    {
        var cacheKey = $"{mode}-{query}-{pageSize}";
        if (_cache.TryGetValue(cacheKey, out List<(List<Replay>, int)> cachedResult))
        {
            if (page < cachedResult.Count)
            {
                var result = cachedResult[page];
                return (result.Item1, result.Item2, true);
            }
        }

        var stopWatch = new Stopwatch();
        stopWatch.Start();
        var queryable = _context.Replays.AsQueryable();

        
        IIncludableQueryable<Player, Replay?>? players;
        IQueryable<int?>? replayIds;
        switch (mode)
        {
            case SearchMode.Map:
                queryable = queryable.Where(x => x.Map.ToLower().Contains(query.ToLower()));
                break;
            case SearchMode.Gamemode:
                queryable = queryable.Where(x => x.Gamemode.ToLower().Contains(query.ToLower()));
                break;
            case SearchMode.ServerId:
                queryable = queryable.Where(x => x.ServerId.ToLower().Contains(query.ToLower()));
                break;
            case SearchMode.Guid:
                players = _context.Players
                    .Where(p => p.PlayerGuid.ToString().ToLower().Contains(query.ToLower()))
                    .Include(p => p.Replay);
                replayIds = players.Select(p => p.ReplayId).Distinct();
                queryable = _context.Replays.Where(r => replayIds.Contains(r.Id));
                break;
            case SearchMode.PlayerIcName:
                players = _context.Players
                    .Where(p => p.PlayerIcName.ToLower().Contains(query.ToLower()))
                    .Include(p => p.Replay);
                replayIds = players.Select(p => p.ReplayId).Distinct();
                queryable = _context.Replays.Where(r => replayIds.Contains(r.Id));
                break;
            case SearchMode.PlayerOocName:
                players = _context.Players
                    .Where(p => p.PlayerOocName.ToLower().Contains(query.ToLower()))
                    .Include(p => p.Replay);
                replayIds = players.Select(p => p.ReplayId).Distinct();
                queryable = _context.Replays.Where(r => replayIds.Contains(r.Id));
                break;
            case SearchMode.RoundEndText:
                // ReSharper disable once EntityFramework.UnsupportedServerSideFunctionCall (its lying, this works)
                queryable = queryable.Where(x => x.RoundEndTextSearchVector.Matches(query));
                break;
            case SearchMode.ServerName:
                queryable = queryable.Where(x => x.ServerName != null && x.ServerName.ToLower().Contains(query.ToLower()));
                break;
            case SearchMode.RoundId:
                queryable = queryable.Where(x => x.RoundId != null && x.RoundId.ToString().Contains(query));
                break;
            default:
                throw new NotImplementedException();
        }
    
        var totalItems = queryable.Count();

        // Get all results and store them in the cache
        var allResults = queryable
            .Include(r => r.RoundEndPlayers)
            .OrderByDescending(r => r.Date ?? DateTime.MinValue)
            .Take(Constants.SearchLimit)
            .ToList();

        var paginatedResults = new List<(List<Replay>, int)>();
        for (int i = 0; i * pageSize < allResults.Count; i++)
        {
            var paginatedList = allResults
                .Skip(i * pageSize)
                .Take(pageSize)
                .ToList();

            paginatedResults.Add((paginatedList, totalItems));
        }

        _cache.Set(cacheKey, paginatedResults, TimeSpan.FromMinutes(5));

        stopWatch.Stop();
        Log.Information("Search took " + stopWatch.ElapsedMilliseconds + "ms.");

        if (page < paginatedResults.Count)
        {
            return (paginatedResults[page].Item1, paginatedResults[page].Item2, false);
        }

        return (new List<Replay>(), 0, false);
    }
    
    private List<Replay> FilterReplays(List<Replay> replays, Guid? accountGuid)
    {
        var requestor = _context.Accounts
            .FirstOrDefault(a => a.Guid == accountGuid);
        
        for (var i = 0; i < replays.Count; i++)
        {
            replays[i] = FilterReplay(replays[i], accountGuid, requestor);
        }
        
        return replays;
    }
    
    private Replay FilterReplay(Replay replay, Guid? accountGuid, Account? requestor = null)
    {
        foreach (var replayRoundEndPlayer in replay.RoundEndPlayers)
        {
            var accountForPlayer = _context.Accounts
                .Include(a => a.Settings)
                .FirstOrDefault(a => a.Guid == replayRoundEndPlayer.PlayerGuid);
            if (accountForPlayer == null)
            {
                continue;
            }

            if (accountForPlayer.Settings.RedactInformation)
            {
                if (replayRoundEndPlayer.PlayerGuid != accountGuid)
                {
                    // If the requestor is an admin, we can show the information.
                    if (requestor == null || !requestor.IsAdmin)
                    {
                        replayRoundEndPlayer.RedactInformation();
                    }
                }
            }
        }
        
        return replay;
    }
}
