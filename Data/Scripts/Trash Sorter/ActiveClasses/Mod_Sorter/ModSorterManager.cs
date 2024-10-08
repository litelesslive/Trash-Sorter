﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Sandbox.ModAPI.Ingame;
using Trash_Sorter.Data.Scripts.Trash_Sorter.BaseClass;
using Trash_Sorter.Data.Scripts.Trash_Sorter.SessionComponent;
using Trash_Sorter.Data.Scripts.Trash_Sorter.StorageClasses;
using VRage.Game;
using VRageMath;
using IMyConveyorSorter = Sandbox.ModAPI.IMyConveyorSorter;
using IMyTerminalBlock = Sandbox.ModAPI.IMyTerminalBlock;

namespace Trash_Sorter.Data.Scripts.Trash_Sorter.ActiveClasses.Mod_Sorter
{
    /// <summary>
    /// The ModConveyorManager class manages conveyor sorters and their custom data.
    /// It tracks sorters, processes custom data, and updates sorter filters based on changes.
    /// </summary>
    internal class ModSorterManager : ModBase
    {
        public HashSet<IMyConveyorSorter> ModSorterCollection;

        public SorterDataStorage SorterDataStorageRef;

        public Dictionary<MyDefinitionId, SorterLimitManager> SorterLimitManagers;

        private readonly Dictionary<string, MyDefinitionId> ItemNameToDefinitionMap;

        private readonly HashSet<IMyTerminalBlock> GuideHasBeenSet;

        private readonly InventoryGridManager InventoryGridManager;

        private readonly Stopwatch watch = new Stopwatch();

        /// <summary>
        /// Initializes a new instance of the ModConveyorManager class and registers events.
        /// </summary>
        /// <param name="sorters">HashSet of sorters.</param>
        /// <param name="mainItemAccess">Main item storage reference.</param>
        /// <param name="inventoryGridManager">Inventory grid manager instance.</param>
        /// <param name="sorterLimitManagers">Dictionary of sorter limit managers.</param>
        /// <param name="nameToDefinition">Dictionary mapping names to item definitions.</param>
        public ModSorterManager(HashSet<IMyConveyorSorter> sorters,
            MainItemStorage mainItemAccess, InventoryGridManager inventoryGridManager,
            Dictionary<MyDefinitionId, SorterLimitManager> sorterLimitManagers,
            Dictionary<string, MyDefinitionId> nameToDefinition)
        {
            watch.Start();
            ModSorterCollection = sorters;
            SorterLimitManagers = sorterLimitManagers;
            SorterDataStorageRef = new SorterDataStorage(nameToDefinition);
            ItemNameToDefinitionMap = mainItemAccess.NameToDefinitionMap;
            InventoryGridManager = inventoryGridManager;
            GuideHasBeenSet = new HashSet<IMyTerminalBlock>();

            // Registering event for sorter addition
            inventoryGridManager.OnModSorterAdded += Add_Sorter;

            // Create entries and initialize sorters

            SorterInit();
            watch.Stop();
            Logger.Log(ClassName,
                $"Initialization took {watch.Elapsed.Milliseconds}ms, amount of trash sorters {ModSorterCollection.Count}");
        }

        // Guide data is made here :)
        /// <summary>
        /// Creates all possible item entries for user reference.
        /// </summary>
        /// <summary>
        /// Initializes each sorter by setting default values and registering events.
        /// </summary>
        private void SorterInit()
        {
            foreach (var sorter in ModSorterCollection)
            {
                Add_Sorter(sorter);
            }
        }

        /// <summary>
        /// Adds a new sorter, sets default values, and registers events.
        /// </summary>
        /// <param name="sorter">The sorter being added.</param>
        private void Add_Sorter(IMyConveyorSorter sorter)
        {
            sorter.SetFilter(MyConveyorSorterMode.Whitelist, new List<MyInventoryItemFilter>());
            sorter.DrainAll = true;


            sorter.OnClose += Sorter_OnClose;
            sorter.CustomNameChanged += Terminal_CustomNameChanged;
            ModSorterCollection.Add(sorter);

            SorterDataStorageRef.AddOrUpdateSorterRawData(sorter);

            Update_Values(sorter);
        }

        /// <summary>
        /// Tries to update the sorter's values if custom data has changed.
        /// </summary>
        /// <param name="sorter">The sorter to update.</param>
        private void Try_Updating_Values(IMyConveyorSorter sorter)
        {
            if (string.IsNullOrWhiteSpace(sorter.CustomData) && SorterDataStorageRef.IsEmpty(sorter))
                return;

            if (!SorterDataStorageRef.HasCustomDataChanged(sorter))
                return;

            Update_Values(sorter);
        }

        /// <summary>
        /// Updates the sorter's filter values based on changes in custom data.
        /// </summary>
        /// <param name="sorter">The sorter whose values are updated.</param>
        private void Update_Values(IMyConveyorSorter sorter)
        {
            bool hasFilterTagBeenFound;
            var wat1 = Stopwatch.StartNew();

            // Track changes and fetch data
            var data = SorterDataStorageRef.TrackChanges(sorter, out hasFilterTagBeenFound);

            Logger.Log(ClassName,
                $"New entries {SorterDataStorageRef.AddedEntries.Count}, removed entries {SorterDataStorageRef.RemovedEntries.Count}, " +
                $"changed entries {SorterDataStorageRef.ChangedEntries.Count}, has filter been found {hasFilterTagBeenFound}");

            // Process the data only if the filter tag was found
            if (hasFilterTagBeenFound)
            {
                wat1.Restart();

                // Prepare a dictionary for fast lookups instead of using IndexOf
                var defIdDictionary = new Dictionary<string, int>();
                for (var i = 0; i < data.Count; i++)
                {
                    var defId = data[i].Split('|')[0].Trim();
                    defIdDictionary[defId] = i;
                }

                // Process removed entries
                foreach (var line in SorterDataStorageRef.RemovedEntries)
                {
                    ProcessDeletedLine(line, sorter);
                }

                // Process added entries
                foreach (var line in SorterDataStorageRef.AddedEntries)
                {
                    string idString;
                    if(string.IsNullOrEmpty(line.Trim())) continue;
                    var newLine = ProcessNewLine(line, sorter, out idString);

                    // Lookup using dictionary for faster access
                    int index;
                    if (defIdDictionary.TryGetValue(idString, out index))
                    {
                        data[index] = newLine;
                    }
                }

                // Process changed entries
                foreach (var sorterChangedData in SorterDataStorageRef.ChangedEntries)
                {
                    var newLine = ProcessChangedLine(sorterChangedData.Key, sorterChangedData.Value, sorter);

                    // Lookup using dictionary for faster access
                    int index;
                    if (defIdDictionary.TryGetValue(sorterChangedData.Key.Trim(), out index))
                    {
                        data[index] = newLine;
                        Logger.Log(ClassName, $"Index found {index} for line {newLine}");
                    }
                    else
                    {
                        Logger.LogError(ClassName, $"Index for changed {newLine} was not found");
                    }
                }
            }

            // Rebuild custom data efficiently using StringBuilder
            var processedString = UsingStringBuilder(data);
            sorter.CustomData = processedString;
            SorterDataStorageRef.AddOrUpdateSorterRawData(sorter);
        }

        public string UsingStringBuilder(List<string> array)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < array.Count; i++)
            {
                sb.Append(array[i]); // Keep original strings, including empty ones

                if (i < array.Count - 1)
                {
                    sb.Append("\r\n"); // Append new line after each element except the last one
                }
            }

            return sb.ToString();
        }


        private double itemRequestAmount;

        private double itemTriggerAmount;

        // Functions to process new/removed/edited lines. TODO MAYBE LOOK INTO OPTIMIZING THIS

        private string ProcessNewLine(string trimmedLine, IMyConveyorSorter sorter, out string idString)
        {
            var parts = trimmedLine.Split(new[] { '|' }, StringSplitOptions.None);
            var firstEntry = parts[0].Trim();
            idString = firstEntry;
            MyDefinitionId definitionId;
            if (!ItemNameToDefinitionMap.TryGetValue(firstEntry, out definitionId))
            {
                Logger.Log(ClassName, $"String invalid {firstEntry}");
                return
                    $"// {firstEntry} is not a valid identifier. Tag [guide] in name for all identifiers";
            }

            switch (parts.Length)
            {
                case 2:
                    double.TryParse(parts[1].Trim(), out itemRequestAmount);
                    if (itemRequestAmount < 0) itemRequestAmount = 0;
                    break;
                case 3:
                    double.TryParse(parts[1].Trim(), out itemRequestAmount);
                    if (!double.TryParse(parts[2].Trim(), out itemTriggerAmount) ||
                        itemTriggerAmount <= itemRequestAmount)
                    {
                        itemTriggerAmount = itemRequestAmount + itemRequestAmount * 0.5;
                    }

                    if (itemRequestAmount < 0) itemRequestAmount = 0;

                    break;
            }

            SorterLimitManager limitManager;
            if (!SorterLimitManagers.TryGetValue(definitionId, out limitManager))
                return "This is bad, you better don't see this line.";

            if (itemRequestAmount == 0) return $"{firstEntry} | {itemRequestAmount} | {itemTriggerAmount}";
            limitManager.RegisterSorter(sorter, itemRequestAmount, itemTriggerAmount);

            // Return the line back for custom data.
            return $"{firstEntry} | {itemRequestAmount} | {itemTriggerAmount}";
        }

        /// <summary>
        /// Processes the deletion of a line corresponding to a sorter limit. 
        /// It un registers the sorter from the SorterLimitManager associated with the given definition.
        /// </summary>
        /// <param name="defId">The MyDefinitionId of the item being removed.</param>
        /// <param name="sorter">The sorter from which the item is being removed.</param>
        private void ProcessDeletedLine(MyDefinitionId defId, IMyConveyorSorter sorter)
        {
            SorterLimitManager limitManager;
            if (!SorterLimitManagers.TryGetValue(defId, out limitManager))
            {
                Logger.LogError(ClassName, "This is bad, you better don't see this line.");
                return;
            }

            limitManager.UnRegisterSorter(sorter);
        }

        /// <summary>
        /// Processes the modification of a line corresponding to a sorter limit. 
        /// It updates or un registers the sorter from the SorterLimitManager based on the parsed data.
        /// </summary>
        /// <param name="defId">The string identifier of the item being modified.</param>
        /// <param name="combinedValue">The value string representing request and trigger amounts.</param>
        /// <param name="sorter">The sorter being updated.</param>
        /// <returns>A formatted string representing the updated line.</returns>
        ///
        /// 
        private string ProcessChangedLine(string defId, string combinedValue, IMyConveyorSorter sorter)
        {
            MyDefinitionId definitionId;
            if (!ItemNameToDefinitionMap.TryGetValue(defId, out definitionId))
            {
                Logger.LogError(ClassName, $"{defId} is not a valid identifier");
                return $"// {defId} is not a valid identifier.";
            }

            // Parse the combined value into parts
            var parts = combinedValue.Split(new[] { '|' }, StringSplitOptions.None);

            // Parse itemRequestAmount if parts exist and it's valid
            if (parts.Length > 0 && (!double.TryParse(parts[0].Trim(), out itemRequestAmount) || itemRequestAmount < 0))
            {
                itemRequestAmount = 0;
            }

            // Parse itemTriggerAmount if parts[1] exists, or calculate default value
            if (parts.Length > 1 && (!double.TryParse(parts[1].Trim(), out itemTriggerAmount) ||
                                     itemTriggerAmount <= itemRequestAmount))
            {
                // Default to 75% higher than itemRequestAmount if invalid or not set
                itemTriggerAmount = itemRequestAmount + Math.Abs(itemRequestAmount) * 0.75;
            }

            // Check if the SorterLimitManager exists for the given definitionId
            SorterLimitManager limitManager;
            if (!SorterLimitManagers.TryGetValue(definitionId, out limitManager))
            {
                Logger.LogError(ClassName, $"Failed to find SorterLimitManager for definition ID: {definitionId}");
                return $"// Error: SorterLimitManager not found for definitionId {definitionId}.";
            }

            // Remove or update sorter limits based on itemRequestAmount
            if (itemRequestAmount == 0)
            {
                Logger.Log(ClassName, $"Removing limit on sorter {sorter.CustomName} for {definitionId.SubtypeName}");
                limitManager.UnRegisterSorter(sorter);
            }
            else
            {
                Logger.Log(ClassName,
                    $"Changing limit on sorter {sorter.CustomName}: Request = {itemRequestAmount}, Trigger = {itemTriggerAmount}");
                limitManager.ChangeLimitsOnSorter(sorter, itemRequestAmount, itemTriggerAmount);
            }

            Logger.Log(ClassName, $"Changed line is: {defId} | {itemRequestAmount} | {itemTriggerAmount}");
            return $"{defId} | {itemRequestAmount} | {itemTriggerAmount}";
        }

        // TODO find why it double triggers. Guide is always found twice. Not really an issue. But still
        /// <summary>
        /// Event handler for when the terminal block's custom name is changed. 
        /// Detects if the guide call is present in the name and replaces it with the guide data.
        /// </summary>
        /// <param name="obj">The terminal block whose name was changed.</param>
        private void Terminal_CustomNameChanged(IMyTerminalBlock obj)
        {
            var name = obj.CustomName;

            if (name.IndexOf(GuideCall, StringComparison.OrdinalIgnoreCase) < 0)
            {
                if (GuideHasBeenSet.Contains(obj)) return;
            }
            else
            {
                if (GuideHasBeenSet.Contains(obj)) GuideHasBeenSet.Remove(obj);
            }

            GuideHasBeenSet.Add(obj);
            Logger.Log(ClassName, $"Sorter guide detected, {name}");
            obj.CustomData = ModSessionComponent.GuideData;
            SorterDataStorageRef.AddOrUpdateSorterRawData((IMyConveyorSorter)obj);
        }

        /// <summary>
        /// Method called after every 100 simulation ticks. It checks and updates values for all sorters in the collection.
        /// </summary>
        public void OnAfterSimulation100()
        {
            foreach (var sorter in ModSorterCollection)
            {
                Try_Updating_Values(sorter);
            }
        }

        /// <summary>
        /// Event handler for when a sorter is closed. Un-registers events and removes the sorter from the collection.
        /// </summary>
        /// <param name="obj">The entity (sorter) that is being closed.</param>
        private void Sorter_OnClose(VRage.ModAPI.IMyEntity obj)
        {
            var terminal = (IMyTerminalBlock)obj;
            obj.OnClose -= Sorter_OnClose;
            terminal.CustomNameChanged -= Terminal_CustomNameChanged;
            var sorter = (IMyConveyorSorter)obj;
            ModSorterCollection.Remove(sorter);
        }

        /// <summary>
        /// Disposes the ModConveyorManager instance and un-registers all events to prevent memory leaks.
        /// </summary>
        public override void Dispose()
        {
            base.Dispose();
            InventoryGridManager.OnModSorterAdded -= Add_Sorter;
            foreach (var sorter in ModSorterCollection.ToList())
            {
                sorter.OnClose -= Sorter_OnClose;
                var terminal = (IMyTerminalBlock)sorter;
                terminal.CustomNameChanged -= Terminal_CustomNameChanged;
            }
        }
    }
}