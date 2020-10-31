﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using System.Reflection;
using TwitchPlays.ScoreMethods;
using System.Text.RegularExpressions;

public class ModuleInformation
{
	public string moduleDisplayName = string.Empty;
	public string moduleID;

	public bool scoreStringOverride;
	public string scoreString = "5";
	public bool announceModule;
	public bool unclaimable;

	public bool helpTextOverride;
	public string helpText;

	public bool manualCodeOverride;
	public string manualCode;

	public StatusLightPosition statusLightPosition;

	public bool validCommandsOverride;
	public string[] validCommands;
	public bool DoesTheRightThing;

	public bool builtIntoTwitchPlays;

	public bool CameraPinningAlwaysAllowed;

	public Color unclaimedColor;

	public float additionalNeedyTime;

	public bool ShouldSerializeadditionalNeedyTime() => additionalNeedyTime > 0;
	public bool ShouldSerializeunclaimedColor() => unclaimedColor != new Color();
	public static bool ShouldSerializebuiltIntoTwitchPlays() => false;
	public bool ShouldSerializevalidCommands() => !builtIntoTwitchPlays;
	public bool ShouldSerializeDoesTheRightThing() => !builtIntoTwitchPlays;
	public bool ShouldSerializevalidCommandsOverride() => !builtIntoTwitchPlays;
	public static bool ShouldSerializeScoreExplanation() => false;

	public ModuleInformation Clone() => (ModuleInformation) MemberwiseClone();

	public string ScoreExplanation => GetScoreMethods(null).ConvertAll(method => method.Description).Join(" + ");

	public List<ScoreMethod> GetScoreMethods(TwitchModule module) => ConvertScoreString(scoreString, module);

	public static List<ScoreMethod> ConvertScoreString(string scoreString, TwitchModule module)
	{
		// UN and T is for unchanged and temporary score which are read normally.
		scoreString = Regex.Replace(scoreString, @"(?:UN )?(\d+)T?", "$1");

		var methods = new List<ScoreMethod>();
		foreach (var factor in scoreString.SplitFull("+"))
		{
			if (factor == "TBD")
				continue;

			var split = factor.SplitFull(" ");
			if (!split.Length.EqualsAny(1, 2))
			{
				DebugHelper.Log("Unknown score string:", scoreString);
				continue;
			}

			var numberString = split[split.Length - 1];
			if (numberString.EndsWith("x")) // To parse "5x" we need to remove the x.
				numberString = numberString.Substring(0, numberString.Length - 1);

			if (!float.TryParse(numberString, out float number))
			{
				DebugHelper.Log("Unknown number:", numberString);
				continue;
			}

			var moduleID = module?.Solver.ModInfo.moduleID;
			var moduleDisplayName = module?.Solver.ModInfo.moduleDisplayName;
			switch (split.Length)
			{
				case 1:
					methods.Add(new BaseScore(number));
					break;

				// S is for special modules which we parse out the multiplier and put it into a dictionary and use later.
				case 2 when split[0] == "S":
					// Multiply the score by two because the default DynamicScorePercentage is 0.5.
					methods.Add(new PerModule(number, module));
					break;

				// PPA is for point per action modules which can be parsed in some cases.
				case 2 when split[0] == "PPA":
					methods.Add(new PerAction(number));
					break;

				default:
					if (module != null)
						DebugHelper.Log($"Unrecognized factor \"{factor}\" for {moduleDisplayName} ({moduleID}).");
					else
						DebugHelper.Log($"Unrecognized factor \"{factor}\".");

					break;
			}
		}

		return methods;
	}

	public T GetScoreMethod<T>() where T : ScoreMethod
	{
		foreach (var method in GetScoreMethods(null))
		{
			if (method.GetType() == typeof(T))
				return (T) method;
		}

		return null;
	}
}

public class FileModuleInformation : ModuleInformation
{
	new public string scoreString;
}

public enum StatusLightPosition
{
	Default,
	TopRight,
	TopLeft,
	BottomRight,
	BottomLeft,
	Center,
}

public static class ModuleData
{
	public static bool DataHasChanged = true;
	private static FileModuleInformation[] lastRead = new FileModuleInformation[0]; // Used to prevent overriding settings that are only controlled by the file.
	private readonly static FieldInfo[] infoFields = typeof(ModuleInformation).GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
	private readonly static FieldInfo[] fileInfoFields = typeof(FileModuleInformation).GetFields();
	public static void WriteDataToFile()
	{
		if (!DataHasChanged || ComponentSolverFactory.SilentMode) return;
		string path = Path.Combine(Application.persistentDataPath, usersSavePath);
		DebugHelper.Log($"Writing file {path}");

		try
		{
			var infoList = ComponentSolverFactory
				.GetModuleInformation()
				.OrderBy(info => info.moduleDisplayName)
				.ThenBy(info => info.moduleID)
				.Select(info =>
				{
					var fileInfo = Array.Find(lastRead, file => file.moduleID == info.moduleID);
					var dictionary = new Dictionary<string, object>();
					foreach (var field in infoFields)
					{
						if (!(info.CallMethod<bool?>($"ShouldSerialize{field.Name}") ?? true))
							continue;

						var value = field.GetValue(info);
						if (field.Name == "scoreString" && fileInfo != null)
							value = fileInfo.scoreString;

						dictionary[field.Name] = value;
					}

					return dictionary;
				})
				.ToList();

			File.WriteAllText(path, SettingsConverter.Serialize(infoList));
		}
		catch (FileNotFoundException)
		{
			DebugHelper.LogWarning($"File {path} was not found.");
			return;
		}
		catch (Exception ex)
		{
			DebugHelper.LogException(ex);
			return;
		}

		DataHasChanged = false;
		DebugHelper.Log($"Writing of file {path} completed successfully.");
	}

	public static bool LoadDataFromFile()
	{
		FileModuleInformation[] modInfo;
		string path = Path.Combine(Application.persistentDataPath, usersSavePath);
		try
		{
			DebugHelper.Log($"Loading Module information data from file: {path}");
			modInfo = SettingsConverter.Deserialize<FileModuleInformation[]>(File.ReadAllText(path));
			lastRead = modInfo;
		}
		catch (FileNotFoundException)
		{
			DebugHelper.LogWarning($"File {path} was not found.");
			return false;
		}
		catch (Exception ex)
		{
			DebugHelper.LogException(ex);
			return false;
		}

		foreach (var fileInfo in modInfo)
		{
			ModuleInformation defaultInfo = null;
			if (fileInfo.moduleID != null)
			{
				defaultInfo = ComponentSolverFactory.GetDefaultInformation(fileInfo.moduleID);
			}

			var info = new ModuleInformation();
			foreach (FieldInfo fileFieldInfo in fileInfoFields)
			{
				if (fileFieldInfo.DeclaringType == typeof(ModuleInformation)) {
					if (fileInfoFields.Any(field => field.DeclaringType == typeof(FileModuleInformation) && field.Name == fileFieldInfo.Name))
						continue;

					fileFieldInfo.SetValue(info, fileFieldInfo.GetValue(fileInfo));
				}
				else
				{
					var baseFieldInfo = Array.Find(infoFields, field => field.Name == fileFieldInfo.Name);
					if (baseFieldInfo == null)
						throw new NotSupportedException("Superclass isn't overriding only base fields.");

					var value = fileFieldInfo.GetValue(fileInfo);
					if (value == null && defaultInfo != null)
					{
						value = baseFieldInfo.GetValue(defaultInfo);
					}

					baseFieldInfo.SetValue(info, value);
				}
			}

			ComponentSolverFactory.AddModuleInformation(info);
		}
		return true;
	}

	public static string usersSavePath = "ModuleInformation.json";
}
