﻿using NLog;
using POGOProtos.Inventory.Item;
using POGOProtos.Networking.Responses;
using PokemonGo.Haxton.Bot.Inventory;
using PokemonGo.Haxton.Bot.Navigation;
using PokemonGo.Haxton.Bot.Utilities;
using System;
using System.Device.Location;
using System.Linq;
using System.Threading.Tasks;

namespace PokemonGo.Haxton.Bot.Bot
{
    public interface IPoGoBot
    {
        bool ShouldRecycleItems { get; set; }
        bool ShouldEvolvePokemon { get; set; }
        bool ShouldTransferPokemon { get; set; }

        void Run();
    }

    public class PoGoBot : IPoGoBot
    {
        private static Logger logger = LogManager.GetCurrentClassLogger();

        private DateTime LuckyEggUsed { get; set; }
        private readonly IPoGoNavigation _navigation;
        private readonly IPoGoInventory _inventory;
        private readonly IPoGoEncounter _encounter;
        private readonly IPoGoFort _fort;
        private readonly IPoGoMap _map;
        private readonly ILogicSettings _settings;

        public bool ShouldRecycleItems { get; set; }
        public bool ShouldEvolvePokemon { get; set; }
        public bool ShouldTransferPokemon { get; set; }

        public PoGoBot(IPoGoNavigation navigation, IPoGoInventory inventory, IPoGoEncounter encounter, IPoGoFort fort, IPoGoMap map, ILogicSettings settings)
        {
            _navigation = navigation;
            _inventory = inventory;
            _encounter = encounter;
            _fort = fort;
            _map = map;
            _settings = settings;

            LuckyEggUsed = DateTime.MinValue;

            ShouldTransferPokemon = _settings.TransferDuplicatePokemon;
            ShouldEvolvePokemon = _settings.EvolveAllPokemonWithEnoughCandy || _settings.EvolveAllPokemonAboveIv;
            ShouldRecycleItems = _settings.ItemRecycleFilter.Count > 0;
        }

        public void Run()
        {
            logger.Info("Starting bot.");
            Task.Run(RecycleItemsTask);
            Task.Run(EvolvePokemonTask);
            Task.Run(TransferDuplicatePokemon);
            Task.Run(FarmPokestopsTask);
        }

        private async Task FarmPokestopsTask()
        {
            while (true)
            {
                var pokestopList = (await _map.GetPokeStops()).ToList();
                var numberOfPokestopsVisited = 0;
                if (!pokestopList.Any())
                {
                    logger.Warn("No pokestops found! Are you sure you're not in the middle ocean?");
                }
                while (pokestopList.Any())
                {
                    logger.Info($"Found {pokestopList.Count} pokestops.");
                    var closestPokestop = pokestopList.OrderBy(
                        i =>
                            LocationUtils.CalculateDistanceInMeters(_navigation.CurrentLatitude,
                                _navigation.CurrentLongitude, i.Latitude, i.Longitude)).First();
                    pokestopList.Remove(closestPokestop);

                    var distance = LocationUtils.CalculateDistanceInMeters(_navigation.CurrentLatitude,
                        _navigation.CurrentLongitude, closestPokestop.Latitude, closestPokestop.Longitude);
                    var pokestop =
                        await _fort.GetFort(closestPokestop.Id, closestPokestop.Latitude, closestPokestop.Longitude);

                    logger.Info($"Moving to {pokestop.Name}, {Math.Round(distance)}m away.");
                    await
                        _navigation.HumanLikeWalking(
                            new GeoCoordinate(closestPokestop.Latitude, closestPokestop.Longitude),
                            _settings.WalkingSpeedInKilometerPerHour, CatchNearbyPokemon);

                    var pokestopBooty =
                        await _fort.SearchFort(closestPokestop.Id, closestPokestop.Latitude, closestPokestop.Longitude);
                    if (pokestopBooty.ExperienceAwarded > 0)
                    {
                        logger.Info(
                            $"[{numberOfPokestopsVisited++}] {pokestop.Name} rewarded us with {pokestopBooty.ExperienceAwarded} exp. {pokestopBooty.GemsAwarded} gems. {StringUtils.GetSummedFriendlyNameOfItemAwardList(pokestopBooty.ItemsAwarded)}.");
                        //_stats.ExperienceSinceStarted += pokestopBooty.ExperienceAwarded;
                        //_stats.
                    }

                    await Task.Delay(1000);
                }
            }
        }

        private void CatchNearbyPokemon()
        {
            var pokemon = _map.GetNearbyPokemonClosestFirst().GetAwaiter().GetResult();
            if (pokemon.Any())
            {
                var pokemonList = string.Join(",", pokemon.Select(x => x.PokemonId).ToArray());
                logger.Info($"{pokemon.Count()} Pokemon found: {pokemonList}");
            }
            foreach (var mapPokemon in pokemon)
            {
                //logger.Info($"Found {pokemon.Count()} pokemon in your area.");
                if (_settings.UsePokemonToNotCatchFilter &&
                    _settings.PokemonsNotToCatch.Contains(mapPokemon.PokemonId))
                {
                    continue;
                }
                Task.Run(async () =>
                {
                    //var distance = LocationUtils.CalculateDistanceInMeters(_navigation.CurrentLatitude, _navigation.CurrentLongitude, mapPokemon.Latitude, mapPokemon.Longitude);
                    await Task.Delay(500);
                    var encounter = await _encounter.EncounterPokemonAsync(mapPokemon);
                    if (encounter.Status == EncounterResponse.Types.Status.EncounterSuccess)
                    {
                        await _encounter.CatchPokemon(encounter, mapPokemon);
                    }
                    else
                    {
                        if (encounter.Status != EncounterResponse.Types.Status.EncounterAlreadyHappened)
                            logger.Warn($"Unable to catch pokemon. Reason: {encounter.Status}");
                    }
                    if (!Equals(pokemon.Last(), mapPokemon))
                    {
                        await Task.Delay(_settings.DelayBetweenPokemonCatch);
                    }
                });
            }
        }

        private async Task TransferDuplicatePokemon()
        {
            while (ShouldTransferPokemon)
            {
                var duplicatePokemon = _inventory.GetDuplicatePokemonForTransfer(_settings.KeepPokemonsThatCanEvolve, _settings.PrioritizeIvOverCp, _settings.PokemonsNotToTransfer);
                foreach (var pokemonData in duplicatePokemon)
                {
                    if (pokemonData.Cp >= _settings.KeepMinCp || PokemonInfo.CalculatePokemonPerfection(pokemonData) > _settings.KeepMinIvPercentage)
                    {
                        continue;
                    }
                    logger.Info($"Transferring pokemon {pokemonData.PokemonId} with cp {pokemonData.Cp}.");
                    await _inventory.TransferPokemon(pokemonData.Id);

                    //var bestPokemon = _settings.PrioritizeIvOverCp
                    //    ? _inventory.GetBestPokemonByIv(pokemonData.PokemonId)
                    //    : _inventory.GetBestPokemonByCp(pokemonData.PokemonId)
                    //    ?? pokemonData;
                }
                await Task.Delay(30000);
            }
        }

        private async Task EvolvePokemonTask()
        {
            while (ShouldEvolvePokemon)
            {
                if (_settings.UseLuckyEggsWhileEvolving)
                {
                    logger.Info("Using lucky egg.");
                    LuckyEgg();
                }
                var pokemon = _inventory.GetPokemonToEvolve(_settings.PokemonsToEvolve).ToList();
                pokemon.ForEach(async p =>
                {
                    logger.Info($"Evolving pokemon {p.PokemonId} with cp {p.Cp}.");
                    await _inventory.EvolvePokemon(p.Id);
                });
                await Task.Delay(30000);
            }
        }

        private async void LuckyEgg()
        {
            if (LuckyEggUsed.AddMinutes(30) < DateTime.Now)
            {
                var inventoryContent = _inventory.Items;

                var luckyEggs = inventoryContent.Where(p => p.ItemId == ItemId.ItemLuckyEgg);
                var luckyEgg = luckyEggs.FirstOrDefault();

                if (luckyEgg == null || luckyEgg.Count <= 0)
                    return;
                logger.Info($"Lucky egg used. {luckyEgg.Count} remaining");
                await _inventory.UseLuckyEgg();
                await Task.Delay(2000);
            }
            logger.Info("Lucky egg not used. Still have one in effect.");
        }

        private async Task RecycleItemsTask()
        {
            while (ShouldRecycleItems)
            {
                var itemsToThrowAway = _inventory.GetItemsToRecycle(_settings.ItemRecycleFilter).ToList();
                itemsToThrowAway.ForEach(async x =>
                {
                    logger.Info($"Recycling item(s): {x.ItemId} x{x.Count}");
                    _inventory.RecycleItems(x.ItemId, x.Count);
                    await Task.Delay(500);
                });
                await Task.Delay(30000);
            }
        }
    }
}