﻿#if __IOS__
using System;
using System.Collections.Generic;
using System.Text;
using Uno.Extensions;
using UIKit;

namespace Windows.Graphics.Display
{
	public sealed partial class BrightnessOverride
	{
		private static UIScreen Window => UIScreen.MainScreen;

		/// <summary>
		/// Sets the brightness level within a range of 0 to 1 and the override options. 
		/// When your app is ready to change the current brightness with what you want to override it with, call StartOverride().
		/// </summary>
		/// <param name="brightnessLevel">double 0 to 1 </param>
		/// <param name="options"></param>
		public void SetBrightnessLevel(double brightnessLevel, DisplayBrightnessOverrideOptions options)
		{
			_targetBrightnessLevel = brightnessLevel.Clamp(0, 1);
		}

		/// <summary>
		/// Request to start overriding the screen brightness level.
		/// </summary>
		public void StartOverride()
		{
			_defaultBrightnessLevel = Window.Brightness;

			Window.Brightness = (float)_targetBrightnessLevel;

			GetForCurrentView().IsOverrideActive = true;
		}

		/// <summary>
		/// Stops overriding the brightness level.
		/// </summary>
		public void StopOverride()
		{
			if (GetForCurrentView().IsOverrideActive)
			{
				Window.Brightness = (float)_defaultBrightnessLevel;
				GetForCurrentView().IsOverrideActive = false;
			}
		}
	}
}
#endif