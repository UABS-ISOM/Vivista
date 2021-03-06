﻿using System;
using System.Linq;
using UnityEngine;

public static class VRDevices
{
	public enum LoadedSdk
	{
		None,
		OpenVr,
		Oculus
	}

	public enum LoadedControllerSet
	{
		NoControllers,
		Vive,
		Oculus
	}

	public static LoadedSdk loadedSdk;
	public static LoadedControllerSet loadedControllerSet;

	public static bool hasLeftController;
	public static bool hasRightController;
	public static bool hasRemote;

	public static void DetectDevices()
	{
		var devices = Input.GetJoystickNames();

		switch (loadedSdk)
		{
			case LoadedSdk.Oculus:
				hasLeftController = devices.Contains("Oculus Touch Controller - Left");
				hasRightController = devices.Contains("Oculus Touch Controller - Right");
				hasRemote = devices.Contains("Oculus Remote");
				loadedControllerSet = LoadedControllerSet.Oculus;
				break;

			case LoadedSdk.OpenVr:
				//NOTE(Kristof): Better way to do this?
				if (devices.Contains("OpenVR Controller(Oculus Rift CV1 (Left Controller)) - Left") || devices.Contains("OpenVR Controller(Oculus Rift CV1 (Right Controller)) - Right"))
				{
					hasLeftController = devices.Contains("OpenVR Controller(Oculus Rift CV1 (Left Controller)) - Left");
					hasRightController = devices.Contains("OpenVR Controller(Oculus Rift CV1 (Right Controller)) - Right");
					//NOTE(kristof): OpenVR doesn't seem to detect the remote
					hasRemote = false;

					loadedControllerSet = LoadedControllerSet.Oculus;
				}
				else if (devices.Contains("OpenVR Controller(Vive. Controller MV) - Left") || devices.Contains("OpenVR Controller(Vive. Controller MV) - Right"))
				{
					hasLeftController = devices.Contains("OpenVR Controller(Vive. Controller MV) - Left");
					hasRightController = devices.Contains("OpenVR Controller(Vive. Controller MV) - Right");

					loadedControllerSet = LoadedControllerSet.Vive;
				}
				else
				{
					hasLeftController = false;
					hasRightController = false;
					hasRemote = false;
					loadedControllerSet = LoadedControllerSet.NoControllers;
				}

				break;

			case LoadedSdk.None:
				break;

			default:
				throw new ArgumentOutOfRangeException();
		}
	}

	public static void SetControllerTutorialMode(GameObject controller, bool enabled)
	{
		GameObject[] controllerArray = { controller };
		SetControllersTutorialMode(controllerArray, enabled);
	}

	public static void SetControllersTutorialMode(GameObject[] controllers, bool enabled)
	{
		foreach (var controller in controllers)
		{
			if (enabled)
			{
				controller.GetComponent<Controller>().TutorialHighlight();
			}
			else
			{
				controller.GetComponent<Controller>().ResetMaterial();
			}
		}
	}
}
