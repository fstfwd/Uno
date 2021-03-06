﻿using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Windows.Foundation;
using Windows.Graphics.Display;

namespace Uno.Extensions
{
	public static class RectExtensions
	{
		/// <summary>
		/// Creates a transformed <see cref="Rect"/> using a <see cref="Matrix3x2"/>.
		/// </summary>
		/// <param name="rect">The rectangle to transform</param>
		/// <param name="m">The matrix to use to transform the <paramref name="rect"/></param>
		/// <returns>A new rectangle</returns>
		internal static Rect Transform(this Rect rect, Matrix3x2 m)
		{
			var leftTop = new Vector2((float)rect.Left, (float)rect.Top);
			var rightBottom = new Vector2((float)rect.Right, (float)rect.Bottom);

			var leftTop2 = Vector2.Transform(leftTop, m);
			var rightBottom2 = Vector2.Transform(rightBottom, m);

			return new Rect(leftTop2.ToPoint(), rightBottom2.ToPoint());
		}

		/// <summary>
		/// Returns the orientation of the rectangle.
		/// </summary>
		/// <param name="rect">A rectangle.</param>
		/// <returns>Portrait, Landscape, or None (if the rectangle has an exact 1:1 ratio)</returns>
		public static DisplayOrientations GetOrientation(this Rect rect)
		{
			if (rect.Height > rect.Width)
			{
				return DisplayOrientations.Portrait;
			}
			else if (rect.Width > rect.Height)
			{
				return DisplayOrientations.Landscape;
			}
			else
			{
				return DisplayOrientations.None;
			}
		}
	}
}
