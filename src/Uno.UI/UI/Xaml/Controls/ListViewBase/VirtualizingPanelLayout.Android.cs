﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using Windows.Foundation;
using System.Linq;
using System.Text;
using Android.Support.V7.Widget;
using Android.Views;
using Nito.Collections;
using Uno.Extensions;
using Uno.UI;
using Uno.Logging;
using System.Runtime.CompilerServices;
using Windows.UI.Xaml.Controls.Primitives;
using Android.Graphics;
using Uno.UI.Extensions;

namespace Windows.UI.Xaml.Controls
{
	public abstract partial class VirtualizingPanelLayout : RecyclerView.LayoutManager, DependencyObject
#if !MONOANDROID6_0 && !MONOANDROID7_0
		, RecyclerView.SmoothScroller.IScrollVectorProvider
#endif
	{
		/// Notes: For the sake of minimizing conditional branches, almost all the layouting logic is carried out relative to the scroll 
		/// direction. To avoid confusion, a number of terms are used in place of terms like 'width' and 'height':
		/// 
		/// Extent: Size along the dimension parallel to scrolling. The equivalent of 'Height' if scrolling is vertical, or 'Width' otherwise.
		/// Breadth: Size along the dimension orthogonal to scrolling. The equivalent of 'Width' if scrolling is vertical, or 'Height' otherwise.
		/// Start: The edge of the element nearest to the top of the content panel, ie 'Top' or 'Left' depending whether scrolling is vertical or horizontal.
		/// End: The edge of the element nearest to the bottom of the content panel, ie 'Bottom' or 'Right' depending whether scrolling is vertical or horizontal.
		/// 
		/// Leading: When scrolling, the edge that is coming into view. ie, if the scrolling forward in a vertical orientation, the bottom edge.
		/// Trailing: When scrolling, the edge that is disappearing from view.

		protected enum FillDirection { Forward, Back }
		protected enum ViewType { Item, GroupHeader, Header, Footer }

		private readonly Deque<Group> _groups = new Deque<Group>();
		private bool _isInitialGroupHeaderCreated;
		private bool _areHeaderAndFooterCreated;
		private bool _isInitialExtentOffsetApplied;
		//The previous item to the old first visible item, used when a lightweight layout rebuild is called
		private IndexPath? _dynamicSeedIndex;
		//Start position of the old first group, used when a lightweight layout rebuild is called
		private int? _dynamicSeedStart;
		// Previous extent of header, used when a lightweight layout rebuild is called
		private int? _previousHeaderExtent;
		/// <summary>
		/// Header and/or footer's content and/or template have changed, they need to be updated.
		/// </summary>
		private bool _needsHeaderAndFooterUpdate;

		internal int Extent => ScrollOrientation == Orientation.Vertical ? Height : Width;
		internal int Breadth => ScrollOrientation == Orientation.Vertical ? Width : Height;
		private int ContentBreadth => Breadth - InitialBreadthPadding - FinalBreadthPadding;

		/// <summary>
		/// The count of views that correspond to collection items (and not group headers, etc)
		/// </summary>
		private int ItemViewCount { get; set; }
		private int GroupHeaderViewCount { get; set; }
		private int HeaderViewCount { get; set; }
		private int FooterViewCount { get; set; }
		/// <summary>
		/// The index of the first child view that is an item (ie not a header/footer/group header).
		/// </summary>
		private int FirstItemView => HeaderViewCount;

		// Item about to be shown after call to ScrollIntoView().
		private ScrollToPositionRequest _pendingScrollToPositionRequest;

		private readonly Queue<ListViewBase.GroupOperation> _pendingGroupOperations = new Queue<ListViewBase.GroupOperation>();

		public VirtualizingPanelLayout()
		{
			ResetLayoutInfo();
		}

		public ListViewBase XamlParent { get; set; }

		private ScrollingViewCache ViewCache => XamlParent?.NativePanel.ViewCache;

		public Orientation Orientation
		{
			get { return (Orientation)GetValue(OrientationProperty); }
			set { SetValue(OrientationProperty, value); }
		}

		public static readonly DependencyProperty OrientationProperty =
			DependencyProperty.Register("Orientation", typeof(Orientation), typeof(VirtualizingPanelLayout), new PropertyMetadata(Orientation.Vertical, (o, e) => ((VirtualizingPanelLayout)o).OnOrientationChanged((Orientation)e.NewValue)));

		private void OnOrientationChanged(Orientation newValue)
		{
			RemoveAllViews();
			//TODO: preserve scroll position
			RequestLayout();
		}

		private Thickness _padding;
		public Thickness Padding
		{
			get => _padding;
			set
			{
				_padding = value;
				RequestLayout();
			}
		}

		private int InitialExtentPadding => (int)ViewHelper.LogicalToPhysicalPixels(ScrollOrientation == Orientation.Vertical ? Padding.Top : Padding.Left);
		private int FinalExtentPadding => (int)ViewHelper.LogicalToPhysicalPixels(ScrollOrientation == Orientation.Vertical ? Padding.Bottom : Padding.Right);
		private int InitialBreadthPadding => (int)ViewHelper.LogicalToPhysicalPixels(ScrollOrientation == Orientation.Vertical ? Padding.Left : Padding.Top);
		private int FinalBreadthPadding => (int)ViewHelper.LogicalToPhysicalPixels(ScrollOrientation == Orientation.Vertical ? Padding.Right : Padding.Bottom);

		public int ContentOffset { get; private set; }
		public int HorizontalOffset => ScrollOrientation == Orientation.Horizontal ? ContentOffset : 0;
		public int VerticalOffset => ScrollOrientation == Orientation.Vertical ? ContentOffset : 0;

		public override RecyclerView.LayoutParams GenerateDefaultLayoutParams()
		{
			return new RecyclerView.LayoutParams(RecyclerView.LayoutParams.WrapContent, RecyclerView.LayoutParams.WrapContent);
		}

		/// <summary>
		/// "Lay out all relevant child views from the given adapter." https://developer.android.com/reference/android/support/v7/widget/RecyclerView.LayoutManager.html#onLayoutChildren(android.support.v7.widget.RecyclerView.Recycler, android.support.v7.widget.RecyclerView.State)
		/// </summary>
		public override void OnLayoutChildren(RecyclerView.Recycler recycler, RecyclerView.State state)
		{
			try
			{
				if (_pendingScrollToPositionRequest != null)
				{
					ApplyScrollToPosition(
						_pendingScrollToPositionRequest.Position, 
						_pendingScrollToPositionRequest.Alignment, 
						recycler, 
						state
					);

					_pendingScrollToPositionRequest = null;
				}
				else
				{
					UpdateLayout(FillDirection.Forward, Extent, ContentBreadth, recycler, state, isMeasure: false);
				}
			}
			catch (Exception e)
			{
				Windows.UI.Xaml.Application.Current.RaiseRecoverableUnhandledExceptionOrLog(e, this);
			}
		}

		public override int ScrollVerticallyBy(int dy, RecyclerView.Recycler recycler, RecyclerView.State state)
		{
			try
			{
				Debug.Assert(ScrollOrientation == Orientation.Vertical, "ScrollOrientation == Orientation.Vertical");

				var actualOffset = ScrollBy(dy, recycler, state);
				OffsetChildrenVertical(-actualOffset);
				return actualOffset;
			}
			catch (Exception e)
			{
				Windows.UI.Xaml.Application.Current.RaiseRecoverableUnhandledExceptionOrLog(e, this);
				return 0;
			}
		}

		public override int ScrollHorizontallyBy(int dx, RecyclerView.Recycler recycler, RecyclerView.State state)
		{
			try
			{
				Debug.Assert(ScrollOrientation == Orientation.Horizontal, "ScrollOrientation == Orientation.Horizontal");

				var actualOffset = ScrollBy(dx, recycler, state);
				OffsetChildrenHorizontal(-actualOffset);
				return actualOffset;
			}
			catch (Exception e)
			{
				Windows.UI.Xaml.Application.Current.RaiseRecoverableUnhandledExceptionOrLog(e, this);
				return 0;
			}
		}

		public override bool CanScrollVertically()
		{
			return ScrollOrientation == Orientation.Vertical;
		}

		public override bool CanScrollHorizontally()
		{
			return ScrollOrientation == Orientation.Horizontal;
		}

		public override void ScrollToPosition(int position)
		{
			ScrollToPosition(position, ScrollIntoViewAlignment.Default);
		}

		internal void ScrollToPosition(int position, ScrollIntoViewAlignment alignment)
		{
			_pendingScrollToPositionRequest = new ScrollToPositionRequest(position, alignment);

			RequestLayout();
		}

		/// <summary>
		/// Apply a requested ScrollToPosition during layouting by calling <see cref="ScrollByInner(int, RecyclerView.Recycler, RecyclerView.State)>"/>
		/// until the requested item is visible.
		/// </summary>
		private void ApplyScrollToPosition(int targetPosition, ScrollIntoViewAlignment alignment, RecyclerView.Recycler recycler, RecyclerView.State state)
		{
			int offsetToApply = 0;
			bool shouldSnapToStart = false;
			bool shouldSnapToEnd = false;

			// 1. Incrementally scroll until target position lies within range of visible positions
			//While target position is after last visible position, scroll forward
			int appliedOffset = 0;
			while (targetPosition > GetLastVisibleDisplayPosition() && GetNextUnmaterializedItem(FillDirection.Forward) != null)
			{
				shouldSnapToEnd = true;
				appliedOffset += GetScrollConsumptionIncrement(FillDirection.Forward);
				offsetToApply += ScrollByInner(appliedOffset, recycler, state);
			}
			//While target position is before first visible position, scroll backward
			while (targetPosition < GetFirstVisibleDisplayPosition() && GetNextUnmaterializedItem(FillDirection.Back) != null)
			{
				shouldSnapToStart = true;
				appliedOffset -= GetScrollConsumptionIncrement(FillDirection.Back);
				offsetToApply += ScrollByInner(appliedOffset, recycler, state);
			}

			var offsetMethod = ScrollOrientation == Orientation.Vertical ? (Action<int>)OffsetChildrenVertical : OffsetChildrenHorizontal;
			offsetMethod(offsetToApply);

			if (alignment == ScrollIntoViewAlignment.Leading)
			{
				shouldSnapToStart = true;
				shouldSnapToEnd = false;
			}

			//2. If view for position lies partially outside visible bounds, bring it into view
			var target = FindViewByAdapterPosition(targetPosition);

			var gapToStart = 0 - GetChildStartWithMargin(target);
			if (!shouldSnapToStart)
			{
				gapToStart = Math.Max(0, gapToStart);
			}
			offsetMethod(gapToStart);

			var gapToEnd = Extent - GetChildEndWithMargin(target);
			if (!shouldSnapToEnd)
			{
				gapToEnd = Math.Min(0, gapToEnd);
			}
			offsetMethod(gapToEnd);

			var snapPosition = GetSnapTo(0, ContentOffset);

			if (snapPosition.HasValue)
			{
				var offset = -GetSnapToAsRemainingDistance(snapPosition.Value);
				offsetMethod(offset);
			}

			//Remove any excess views
			UnfillLayout(FillDirection.Forward, 0, Extent, recycler, state);
			UnfillLayout(FillDirection.Back, 0, Extent, recycler, state);
			FillLayout(FillDirection.Forward, 0, Extent, ContentBreadth, recycler, state);
			FillLayout(FillDirection.Back, 0, Extent, ContentBreadth, recycler, state);
		}
		
		private class ScrollToPositionRequest
		{
			public int Position { get; }

			public ScrollIntoViewAlignment Alignment { get; }

			public ScrollToPositionRequest(int position, ScrollIntoViewAlignment alignment)
			{
				Position = position;
				Alignment = alignment;
			}
		}

		internal int GetSnapToAsRemainingDistance(float snapTo)
		{
			var alignment = SnapPointsAlignment;
			float targetOffset;
			switch (alignment)
			{
				case SnapPointsAlignment.Near:
					targetOffset = snapTo;
					break;
				case SnapPointsAlignment.Center:
					targetOffset = snapTo - Extent / 2f;
					break;
				case SnapPointsAlignment.Far:
					targetOffset = snapTo - Extent;
					break;
				default:
					throw new InvalidOperationException();
			}

			return (int)(targetOffset - ContentOffset);
		}


		/// <summary>
		/// Removes all child views and wipes the internal state of the <see cref="VirtualizingPanelLayout"/>.
		/// </summary>
		public override void RemoveAllViews()
		{
			base.RemoveAllViews();
			ContentOffset = 0;

			ResetLayoutInfo();

			GroupHeaderViewCount = 0;
			HeaderViewCount = 0;
			FooterViewCount = 0;
			ItemViewCount = 0;

			_pendingScrollToPositionRequest = null;
		}

		/// <summary>
		/// Called when the owner <see cref="NativeListViewBase"/> is measured. Materializes items in order to determine how much space is desired.
		/// </summary>
		public override void OnMeasure(RecyclerView.Recycler recycler, RecyclerView.State state, int widthSpec, int heightSpec)
		{
			try
			{
				var availableWidth = ViewHelper.PhysicalSizeFromSpec(widthSpec);
				var availableHeight = ViewHelper.PhysicalSizeFromSpec(heightSpec);

				//Extent == dimension parallel to scroll, breadth == dimension orthogonal to scroll
				var extent = ScrollOrientation == Orientation.Vertical ? availableHeight : availableWidth;
				var totalBreadth = ScrollOrientation == Orientation.Vertical ? availableWidth : availableHeight;
				var breadth = totalBreadth - InitialBreadthPadding - FinalBreadthPadding;
				if (totalBreadth > 0)
				{
					//Populate the panel with items
					UpdateLayout(FillDirection.Forward, extent, breadth, recycler, state, isMeasure: true);
				}

				int measuredWidth, measuredHeight;

				var contentBreadth = _groups.Count > 0 ? _groups.Max(g => g.Breadth) : 0;
				var measuredBreadth = contentBreadth + InitialBreadthPadding + FinalBreadthPadding;

				if (ScrollOrientation == Orientation.Vertical)
				{
					measuredWidth = Math.Min(measuredBreadth, availableWidth);
					measuredHeight = Math.Min(GetContentEnd(), availableHeight);
				}
				else
				{
					measuredWidth = Math.Min(GetContentEnd(), availableWidth);
					measuredHeight = Math.Min(measuredBreadth, availableHeight);
				}
				SetMeasuredDimension(measuredWidth, measuredHeight);
			}
			catch (Exception e)
			{
				Windows.UI.Xaml.Application.Current.RaiseRecoverableUnhandledExceptionOrLog(e, this);
			}
		}

		/// <summary>
		/// "Offset all child views attached to the parent RecyclerView by dx pixels along the horizontal axis." https://developer.android.com/reference/android/support/v7/widget/RecyclerView.LayoutManager.html#offsetChildrenHorizontal(int)
		/// </summary>
		public override void OffsetChildrenHorizontal(int dx)
		{
			base.OffsetChildrenHorizontal(dx);
			Debug.Assert(ScrollOrientation == Orientation.Horizontal);
			ApplyOffset(dx);
		}

		/// <summary>
		/// "Offset all child views attached to the parent RecyclerView by dy pixels along the vertical axis." https://developer.android.com/reference/android/support/v7/widget/RecyclerView.LayoutManager.html#offsetChildrenVertical(int)
		/// </summary>
		public override void OffsetChildrenVertical(int dy)
		{
			base.OffsetChildrenVertical(dy);
			Debug.Assert(ScrollOrientation == Orientation.Vertical);
			ApplyOffset(dy);
		}

		public override int ComputeHorizontalScrollExtent(RecyclerView.State state)
		{
			return ComputeScrollExtent(state);
		}

		public override int ComputeHorizontalScrollOffset(RecyclerView.State state)
		{
			return ComputeScrollOffset(state);
		}

		public override int ComputeHorizontalScrollRange(RecyclerView.State state)
		{
			return ComputeScrollRange(state);
		}

		public override int ComputeVerticalScrollExtent(RecyclerView.State state)
		{
			return ComputeScrollExtent(state);
		}

		public override int ComputeVerticalScrollOffset(RecyclerView.State state)
		{
			return ComputeScrollOffset(state);
		}

		public override int ComputeVerticalScrollRange(RecyclerView.State state)
		{
			return ComputeScrollRange(state);
		}

		public override bool OnRequestChildFocus(RecyclerView parent, RecyclerView.State state, View child, View focused)
		{
			// Returning true here prevents the list scrolling a focussed control into view. We disable this behaviour to prevent a tricky 
			// bug where, when there is a ScrapLayout while scrolling the list, a SelectorItem that has focus is detached and reattached 
			// and the list tries to bring it into view, causing funky 'pinning' behaviour.
			return true;
		}

		public PointF ComputeScrollVectorForPosition(int targetPosition)
		{
			// If target is out-of-viewport, find its direction, otherwise return 0.
			int direction = 0;
			if (targetPosition < GetFirstVisibleDisplayPosition())
			{
				direction = -1;
			}
			else if (targetPosition > GetLastVisibleDisplayPosition())
			{
				direction = 1;
			}

			return GetScrollVector(direction);
		}

		private PointF GetScrollVector(int scrollDirection)
		{
			if (ScrollOrientation == Orientation.Vertical)
			{
				return new PointF(0, scrollDirection);
			}
			else
			{
				return new PointF(scrollDirection, 0);
			}
		}

		/// <summary>
		/// Find view by its 'adapter position' (current position in the collection, versus current laid-out position). These are different 
		/// when a collection change is in process.
		/// </summary>
		/// <param name="position">The adapter position</param>
		/// <returns>Container matching the provided adapter position, if currently visible.</returns>
		public View FindViewByAdapterPosition(int position)
		{
			var childCount = ChildCount;
			for (int i = 0; i < childCount; i++)
			{
				var child = GetChildAt(i);
				var vh = XamlParent?.NativePanel?.GetChildViewHolder(child);

				if (vh == null)
				{
					continue;
				}

				if (vh.AdapterPosition != position)
				{
					continue;
				}

				var byLayoutPosition = FindViewByPosition(vh.LayoutPosition);
				if (byLayoutPosition != child)
				{
					// We call the native method to apply checks on internal properties (shouldIgnore, isRemoved etc)
					return null;
				}

				return child;
			}

			return null;
		}

		internal void Refresh()
		{
			RemoveAllViews();
			RequestLayout();
		}

		/// <summary>
		/// Informs the layout that a INotifyCollectionChanged information has added/removed groups in the source.
		/// </summary>
		internal void NotifyGroupOperation(ListViewBase.GroupOperation pendingOperation)
		{
			_pendingGroupOperations.Enqueue(pendingOperation);
		}

		/// <summary>
		/// The currently-displayed extent, ie the viewport size.
		/// </summary>
		private int ComputeScrollExtent(RecyclerView.State state)
		{
			return Extent;
		}

		/// <summary>
		/// The scrolled offset.
		/// </summary>
		private int ComputeScrollOffset(RecyclerView.State state)
		{
			return ContentOffset;
		}

		/// <summary>
		/// The total range of all content (necessarily an estimate since we can't measure non-materialized items.)
		/// </summary>
		private int ComputeScrollRange(RecyclerView.State state)
		{
			//Assume as a dirt-simple heuristic that all items are uniform. Could refine this to only estimate for unmaterialized content.
			var leadingLine = GetLeadingNonEmptyGroup(FillDirection.Forward)?.GetLeadingLine(FillDirection.Forward);
			if (leadingLine == null)
			{
				return 0;
			}
			var lastItemFlat = GetFlatItemIndex(leadingLine.LastItem);
			var remainingItems = state.ItemCount - XamlParent.NumberOfDisplayGroups - lastItemFlat - 1;
			var remainingLines = remainingItems / leadingLine.NumberOfViews;
			var remainingItemExtent = remainingLines * leadingLine.Extent;

			int remainingGroupExtent = 0;
			if (XamlParent.NumberOfDisplayGroups > 0 && RelativeGroupHeaderPlacement == RelativeHeaderPlacement.Inline)
			{
				var lastGroup = GetLeadingGroup(FillDirection.Forward);
				var remainingGroups = XamlParent.NumberOfDisplayGroups - lastGroup.GroupIndex - 1;
				remainingGroupExtent = remainingGroups * lastGroup.HeaderExtent;
			}

			var range = ContentOffset + remainingItemExtent + remainingGroupExtent +
				//TODO: An inline group header might actually be the view at the bottom of the viewport, we should take this into account
				GetChildEndWithMargin(base.GetChildAt(FirstItemView + ItemViewCount - 1));
			Debug.Assert(range > 0, "Must report a non-negative scroll range.");
			Debug.Assert(remainingItems == 0 || range > Extent, "If any items are non-visible, the content range must be greater than the viewport extent.");
			return range;
		}

		/// <summary>
		/// Update the internal state of the layout, as well as 'floating' views like group headers, when the scrolled offset changes.
		/// </summary>
		private void ApplyOffset(int delta)
		{
			ContentOffset -= delta;
			foreach (var group in _groups)
			{
				group.Start += delta;
			}
			UpdateGroupHeaderPositions();
			UpdateHeaderAndFooterPositions();
		}

		/// <summary>
		/// Adjust Header and Footer positions to be outside the range of collection items at all times.
		/// </summary>
		private void UpdateHeaderAndFooterPositions()
		{
			if (_groups.Count == 0)
			{
				//No other items, therefore correct Header/Footer positions have not shifted.
				return;
			}

			if (HeaderViewCount > 0)
			{
				var header = GetChildAt(GetHeaderViewIndex());
				var delta = GetTrailingGroup(FillDirection.Forward).Start - GetChildEndWithMargin(header);
				OffsetChildAlongExtent(header, delta);
			}

			if (FooterViewCount > 0)
			{
				var footer = GetChildAt(GetFooterViewIndex());
				var delta = GetLeadingGroup(FillDirection.Forward).End - GetChildStartWithMargin(footer);
				OffsetChildAlongExtent(footer, delta);
			}
		}

		/// <summary>
		/// Update group header positions, either because they should 'stick' or because the best guess of their 'clamped' position has changed.
		/// </summary>
		private void UpdateGroupHeaderPositions()
		{
			if (_groups.Count == 0)
			{
				return;
			}

			if (GroupHeaderViewCount == 0)
			{
				//No group header views
				return;
			}

			//Clamp headers based on group bounds
			for (int i = 0; i < _groups.Count; i++)
			{
				var group = _groups[i];
				var groupHeader = GetGroupHeaderAt(i);

				int actualDelta;
				//1. Start with frame if header were inline
				int start = group.Start;

				// Update sticky group headers(if any) to their appropriate(ie, 'stuck') positions
				if (AreStickyGroupHeadersEnabled)
				{
					//2. If frame would be out of bounds, bring it just in bounds
					int clampedStart = Math.Max(start, 0);
					int clampingDelta = clampedStart - GetChildStartWithMargin(groupHeader);
					//3. If frame base would be below base of lowest element in section, bring it just above lowest element in section
					int baseOfGroupDelta = group.End - GetChildEndWithMargin(groupHeader);
					actualDelta = Math.Min(clampingDelta, baseOfGroupDelta);
				}
				// Update position of non-sticky group headers
				else
				{
					//2. Bring header to current start of group
					actualDelta = start - GetChildStartWithMargin(groupHeader);
				}

				OffsetChildAlongExtent(groupHeader, actualDelta);
			}
		}

		private void OffsetChildAlongExtent(View view, int offset)
		{
			if (ScrollOrientation == Orientation.Vertical)
			{
				view.OffsetTopAndBottom(offset);
			}
			else
			{
				view.OffsetLeftAndRight(offset);
			}
		}

		/// <summary>
		/// Check if this view can be scrolled horizontally in a certain direction.
		/// </summary>
		/// <param name="direction">Negative to check scrolling left, positive to check scrolling right.</param>
		internal bool CanCurrentlyScrollHorizontally(int direction) => CanScrollHorizontally() && CanCurrentlyScroll(direction);

		/// <summary>
		/// Check if this view can be scrolled vertically in a certain direction.
		/// </summary>
		/// <param name="direction">Negative to check scrolling up, positive to check scrolling down.</param>
		internal bool CanCurrentlyScrollVertically(int direction) => CanScrollVertically() && CanCurrentlyScroll(direction);

		private bool CanCurrentlyScroll(int direction)
		{
			if (direction < 0)
			{
				return GetContentStart() < 0;
			}
			else
			{
				return GetContentEnd() > Extent;
			}
		}

		/// <summary>
		/// Wipes stored layout information. 
		/// </summary>
		protected virtual void ResetLayoutInfo()
		{
			_groups.Clear();
			_groups.AddToBack(new Group(groupIndex: 0));

			_isInitialGroupHeaderCreated = false;
			_areHeaderAndFooterCreated = false;
			_isInitialExtentOffsetApplied = false;
			_needsHeaderAndFooterUpdate = false;

			ViewCache?.EmptyAndRemove();

			_pendingGroupOperations.Clear();
		}

		/// <summary>
		/// Add view and layout it with a particular offset.
		/// </summary>
		/// <returns>Child's frame in logical pixels, including its margins</returns>
		protected Size AddViewAtOffset(View child, FillDirection direction, int extentOffset, int breadthOffset, int availableBreadth, ViewType viewType = ViewType.Item)
		{
			AddView(child, direction, viewType);


			Size slotSize;
			var logicalAvailableBreadth = ViewHelper.PhysicalToLogicalPixels(availableBreadth);
			if (ScrollOrientation == Orientation.Vertical)
			{
				slotSize = new Size(logicalAvailableBreadth, double.PositiveInfinity);
			}
			else
			{
				slotSize = new Size(double.PositiveInfinity, logicalAvailableBreadth);
			}
			var size = _layouter.MeasureChild(child, slotSize);

			size = ApplyChildStretch(size, slotSize, viewType);

			LayoutChild(child, direction, extentOffset, breadthOffset, size);

			return size;
		}

		/// <summary>
		/// Apply appropriate stretch to measured size return by child view.
		/// </summary>
		protected virtual Size ApplyChildStretch(Size childSize, Size slotSize, ViewType viewType)
		{
			// Group headers positioned adjacent relative to scroll direction shouldn't be stretched
			if (viewType == ViewType.GroupHeader && RelativeGroupHeaderPlacement == RelativeHeaderPlacement.Adjacent)
			{
				return childSize;
			}

			// Apply stretch
			switch (ScrollOrientation)
			{
				case Orientation.Vertical:
					childSize.Width = slotSize.Width;
					break;
				case Orientation.Horizontal:
					childSize.Height = slotSize.Height;
					break;
			}

			return childSize;
		}

		/// <summary>
		/// Layout child view at desired offsets.
		/// </summary>
		protected void LayoutChild(View child, FillDirection direction, int extentOffset, int breadthOffset, Size size)
		{
			var logicalBreadthOffset = ViewHelper.PhysicalToLogicalPixels(breadthOffset);
			var logicalExtentOffset = ViewHelper.PhysicalToLogicalPixels(extentOffset);

			double left, top;
			const double eps = 1e-8;
			if (ScrollOrientation == Orientation.Vertical)
			{

				left = logicalBreadthOffset;
				// Subtracting a very small number mitigates floating point errors when converting negative numbers between physical and logical pixels (because it can happen that a/b*b != a)
				top = direction == FillDirection.Forward ? logicalExtentOffset : logicalExtentOffset - size.Height - eps;
			}
			else
			{
				left = direction == FillDirection.Forward ? logicalExtentOffset : logicalExtentOffset - size.Width - eps;
				top = logicalBreadthOffset;
			}
			var frame = new Windows.Foundation.Rect(new Windows.Foundation.Point(left, top), size);
			_layouter.ArrangeChild(child, frame);

			Debug.Assert(direction == FillDirection.Forward || GetChildEndWithMargin(child) == extentOffset, GetAssertMessage("Extent offset not applied correctly"));
		}

		/// <summary>
		/// Adds a child view to the list in either the leading or trailing direction, incrementing the count of the corresponding
		/// view type and the position of <see cref="FirstItemView"/> as appropriate.
		/// </summary>
		protected void AddView(View child, FillDirection direction, ViewType viewType = ViewType.Item)
		{
			int viewIndex = 0;
			if (direction == FillDirection.Forward && viewType == ViewType.Item)
			{
				viewIndex = FirstItemView + ItemViewCount;
			}
			if (direction == FillDirection.Back && viewType == ViewType.Item)
			{
				viewIndex = FirstItemView;
			}
			if (direction == FillDirection.Forward && viewType == ViewType.GroupHeader)
			{
				viewIndex = FirstItemView + ItemViewCount + GroupHeaderViewCount;
			}
			if (direction == FillDirection.Back && viewType == ViewType.GroupHeader)
			{
				viewIndex = FirstItemView + ItemViewCount;
			}
			if (viewType == ViewType.Header)
			{
				viewIndex = 0;
			}
			if (viewType == ViewType.Footer)
			{
				viewIndex = ChildCount;
			}
			base.AddView(child, viewIndex);
			Debug.Assert(GetChildAt(viewIndex) == child, "GetChildAt(viewIndex) == child");
			if (viewType == ViewType.GroupHeader)
			{
				GroupHeaderViewCount++;
			}
			if (viewType == ViewType.Header)
			{
				HeaderViewCount++;
			}
			if (viewType == ViewType.Footer)
			{
				FooterViewCount++;
			}
			if (viewType == ViewType.Item)
			{
				ItemViewCount++;
			}
		}

		/// <summary>
		/// Called during scrolling, sets the layout according to the requested scroll offset.
		/// </summary>
		/// <returns>The actual amount scrolled (which may be less than requested if the end of the list is reached).</returns>
		private int ScrollBy(int offset, RecyclerView.Recycler recycler, RecyclerView.State state)
		{
			var fillDirection = offset >= 0 ? FillDirection.Forward : FillDirection.Back;
			int unconsumedOffset = offset;
			int actualOffset = 0;
			int appliedOffset = 0;
			var consumptionIncrement = GetScrollConsumptionIncrement(fillDirection) * Math.Sign(offset);
			while (Math.Abs(unconsumedOffset) > Math.Abs(consumptionIncrement))
			{
				//Consume the scroll offset in bite-sized chunks to allow us to recycle views at the same rate as we create them. A big optimization, for 
				//large scroll offsets (ie when calling ScrollIntoView), would be to 'guess' the number of items we will have scrolled and avoid measuring and layouting 
				//the intervening views. This would require modifications to the group layouting logic, which currently assumes we measure the group contents
				//entirely when scrolling forward.
				unconsumedOffset -= consumptionIncrement;
				appliedOffset += consumptionIncrement;
				actualOffset = ScrollByInner(appliedOffset, recycler, state);
			}
			actualOffset = ScrollByInner(offset, recycler, state);

			return actualOffset;
		}

		private int GetScrollConsumptionIncrement(FillDirection fillDirection)
		{
			if (ItemViewCount > 0)
			{
				return GetChildExtentWithMargins(GetLeadingItemView(fillDirection));
			}
			else
			{
				//No children are materialized, this can occur when header/group header is larger than viewport. Just use the first child.
				return GetChildExtentWithMargins(0);
			}
		}

		/// <summary>
		/// Materialize and dematerialize views corresponding to their visibility after the requested scroll offset.
		/// </summary>
		/// <returns>The actual scroll offset (which may be less than requested if the end of the list is reached).</returns>
		private int ScrollByInner(int offset, RecyclerView.Recycler recycler, RecyclerView.State state)
		{
			var fillDirection = offset >= 0 ? FillDirection.Forward : FillDirection.Back;

			//Add newly visible views
			FillLayout(fillDirection, offset, Extent, ContentBreadth, recycler, state);

			int maxPossibleDelta;
			if (fillDirection == FillDirection.Forward)
			{
				// If this value is negative, collection dimensions are larger than all children and we should not scroll
				maxPossibleDelta = Math.Max(0, GetContentEnd() - Extent);
			}
			else
			{
				maxPossibleDelta = GetContentStart() - 0;
			}
			maxPossibleDelta = Math.Abs(maxPossibleDelta);
			var actualOffset = MathEx.Clamp(offset, -maxPossibleDelta, maxPossibleDelta);

			//Remove all views that will be hidden after the actual scroll amount
			UnfillLayout(fillDirection, actualOffset, Extent, recycler, state);

			XamlParent?.TryLoadMoreItems(LastVisibleIndex);

			return actualOffset;
		}

		/// <summary>
		/// Fills in visible views and unfills invisible views from the list.
		/// </summary>
		/// <param name="direction">The fill direction.</param>
		/// <param name="availableExtent">The available extent (dimension of the viewport parallel to the scroll direction).</param>
		/// <param name="availableBreadth">The available breadth (dimension of the viewport orthogonal to the scroll direction).</param>
		/// <param name="recycler">Supplied recycler.</param>
		/// <param name="state">Supplied state object.</param>
		private void UpdateLayout(FillDirection direction, int availableExtent, int availableBreadth, RecyclerView.Recycler recycler, RecyclerView.State state, bool isMeasure)
		{
			if (_needsHeaderAndFooterUpdate)
			{
				ResetHeaderAndFooter(recycler);
				_needsHeaderAndFooterUpdate = false;
			}
			if (isMeasure && state.WillRunSimpleAnimations())
			{
				// When an item is added/removed via an INotifyCollectionChanged operation, the RecyclerView expects two layouts: one 'before' the 
				// operation, and one 'after.' Here we provide the 'before' by very simply not modifying the layout at all.
				return;
			}
			if (isMeasure && availableExtent > 0 && availableBreadth > 0 && ChildCount > 0)
			{
				//Always rebuild the layout on measure, because child dimensions may have changed
				ScrapLayout(recycler, availableBreadth);
			}
			else if (state.WillRunSimpleAnimations())
			{
				//An INotifyCollectionChanged operation is triggering an animated update of the list.
				ScrapLayout(recycler, availableBreadth);
			}
			FillLayout(direction, 0, availableExtent, availableBreadth, recycler, state);
			UnfillLayout(direction, 0, availableExtent, recycler, state);
			UpdateHeaderAndFooterPositions();
			UpdateGroupHeaderPositions();

			XamlParent?.TryLoadMoreItems(LastVisibleIndex);

			UpdateScrollPosition(recycler, state);
		}

		/// <summary>
		/// Scroll to close the gap between the end of the content and the end of the panel if any.
		/// </summary>
		private void UpdateScrollPosition(RecyclerView.Recycler recycler, RecyclerView.State state)
		{
			if (XamlParent?.NativePanel != null && XamlParent.NativePanel.ChildCount > 0)
			{
				var gapToEnd = Extent - GetContentEnd();

				if (gapToEnd > 0)
				{
					if (ScrollOrientation == Orientation.Vertical)
					{
						ScrollVerticallyBy(-gapToEnd, recycler, state);
						XamlParent.NativePanel.OnScrolled(0, -gapToEnd);
					}
					else
					{
						ScrollHorizontallyBy(-gapToEnd, recycler, state);
						XamlParent.NativePanel.OnScrolled(-gapToEnd, 0);
					}
				}
			}
		}

		/// <summary>
		/// Fills in visible views, using the strategy of creating new views in the desired fill direction as long as there is (a) available 
		/// fill space and (b) available items. 
		/// Also initializes header, footer, and internal state if need be.
		/// </summary>
		private void FillLayout(FillDirection direction, int scrollOffset, int availableExtent, int availableBreadth, RecyclerView.Recycler recycler, RecyclerView.State state)
		{
			int extentOffset = scrollOffset;
			var isGrouping = XamlParent?.IsGrouping ?? false;
			var headerOffset = 0;
			if (!_areHeaderAndFooterCreated)
			{
				headerOffset = CreateHeaderAndFooter(extentOffset, InitialBreadthPadding, availableBreadth, recycler, state);
				extentOffset += headerOffset;
				_areHeaderAndFooterCreated = true;
			}

			if (!_isInitialExtentOffsetApplied)
			{
				var group = GetTrailingGroup(direction);
				if (group != null)
				{
					Debug.Assert(group.Lines.Count == 0, "group.Lines.Count == 0");

					// Updating after a ScrapLayout, remove previous header extent before we apply new header extent.
					if (_previousHeaderExtent.HasValue)
					{
						group.Start -= _previousHeaderExtent.Value;
					}

					group.Start += headerOffset;
				}
				else if (_dynamicSeedStart.HasValue && _previousHeaderExtent.HasValue)
				{
					_dynamicSeedStart = _dynamicSeedStart.Value - _previousHeaderExtent.Value + headerOffset;
				}
				_previousHeaderExtent = null;
				_isInitialExtentOffsetApplied = true;
			}
			if (!_isInitialGroupHeaderCreated && isGrouping && XamlParent.NumberOfDisplayGroups > 0)
			{
				CreateGroupHeader(direction, InitialBreadthPadding, availableBreadth, recycler, state, GetLeadingGroup(direction));
				_isInitialGroupHeaderCreated = true;
			}

			var nextItemPath = GetNextUnmaterializedItem(direction, _dynamicSeedIndex ?? GetLeadingMaterializedItem(direction));
			while (nextItemPath != null)
			{
				//Handle the case there are no groups, this may happen during a lightweight rebuild of the layout.
				if (_groups.Count == 0)
				{
					CreateGroupsAtLeadingEdge(nextItemPath.Value.Section, direction, scrollOffset, availableExtent, availableBreadth, recycler, state);
				}
				var createdLine = TryCreateLine(direction, scrollOffset, availableExtent, availableBreadth, recycler, state, nextItemPath.Value);
				if (!createdLine) { break; }
				nextItemPath = GetNextUnmaterializedItem(direction);
			}
			_dynamicSeedIndex = null;

			if (nextItemPath == null && isGrouping)
			{
				var endGroupIndex = direction == FillDirection.Forward ? XamlParent.NumberOfDisplayGroups - 1 : 0;
				if (endGroupIndex != GetLeadingGroup(direction)?.GroupIndex && endGroupIndex >= 0)
				{
					//Create empty groups at start/end
					CreateGroupsAtLeadingEdge(endGroupIndex, direction, scrollOffset, availableExtent, availableBreadth, recycler, state);
				}
			}

			AssertValidState();
		}

		/// <summary>
		/// Checks if there is available space and, if so, materializes a new <see cref="Line"/> (as well as a new <see cref="Group"/> if 
		/// the new line is in a different group).
		/// </summary>
		/// <returns>True if a new line was created, false otherwise.</returns>
		private bool TryCreateLine(FillDirection fillDirection,
			int scrollOffset,
			int availableExtent,
			int availableBreadth,
			RecyclerView.Recycler recycler,
			RecyclerView.State state,
			IndexPath nextVisibleItem
		)
		{
			var leadingGroup = GetLeadingGroup(fillDirection);

			var itemBelongsToGroup = leadingGroup.GroupIndex == nextVisibleItem.Section;
			if (itemBelongsToGroup)
			{
				if (IsThereAGapWithinGroup(leadingGroup, fillDirection, scrollOffset, availableExtent))
				{
					AddLine(fillDirection, availableBreadth, recycler, state, nextVisibleItem);
					return true;
				}
				return false;
			}
			else
			{
				if (IsThereAGapOutsideGroup(leadingGroup, fillDirection, scrollOffset, availableExtent))
				{
					CreateGroupsAtLeadingEdge(nextVisibleItem.Section, fillDirection, scrollOffset, availableExtent, availableBreadth, recycler, state);
					var newLeadingGroup = GetLeadingGroup(fillDirection);
					//Check that leading group is the target (we may have created empty groups) and there is space for items
					if (newLeadingGroup.GroupIndex == nextVisibleItem.Section && IsThereAGapWithinGroup(newLeadingGroup, fillDirection, scrollOffset, availableExtent))
					{
						AddLine(fillDirection, availableBreadth, recycler, state, nextVisibleItem);
						return true;
					}
				}
				return false;
			}
		}

		/// <summary>
		/// Materializes a new line in the desired fill direction and adds it to the corresponding group.
		/// </summary>
		private void AddLine(FillDirection fillDirection,
			int availableBreadth,
			RecyclerView.Recycler recycler,
			RecyclerView.State state,
			IndexPath nextVisibleItem
		)
		{
			var group = GetLeadingGroup(fillDirection);
			var line = CreateLine(fillDirection,
				GetLeadingEdgeWithinGroup(group, fillDirection),
				group.ItemsBreadthOffset + InitialBreadthPadding,
				availableBreadth,
				recycler,
				state,
				nextVisibleItem,
				group.Lines.Count == 0
			);
			group.AddLine(line, fillDirection);
		}

		/// <summary>
		/// Create a single row or column
		/// </summary>
		/// <param name="fillDirection">The direction we're filling in new views</param>
		/// <param name="extentOffset">Extent offset relative to the origin of the panel's bounds</param>
		/// <param name="breadthOffset">Breadth offset relative to the origin of the panel's bounds</param>
		/// <param name="availableBreadth">The breadth available for the line</param>
		/// <param name="recycler">Provided recycler</param>
		/// <param name="state">Provided <see cref="RecyclerView.State"/></param>
		/// <param name="nextVisibleItem">The first item in the line to draw (or the last, if we're filling backwards)</param>
		/// <param name="isNewGroup">Whether this is the first line materialized in a new group.</param>
		/// <returns>An object containing information about the created line.</returns>
		protected abstract Line CreateLine(FillDirection fillDirection,
			int extentOffset,
			int breadthOffset,
			int availableBreadth,
			RecyclerView.Recycler recycler,
			RecyclerView.State state,
			IndexPath nextVisibleItem,
			bool isNewGroup
		);

		/// <summary>
		/// Add a new non-empty group to the internal state of the layout. It will be added at the end if filling forward or the start if 
		/// filling backward. Any intervening empty groups will also be added.
		/// </summary>
		private void CreateGroupsAtLeadingEdge(
			int targetGroupIndex,
			FillDirection fillDirection,
			int scrollOffset,
			int availableExtent,
			int availableBreadth,
			RecyclerView.Recycler recycler,
			RecyclerView.State state
		)
		{
			var leadingGroup = GetLeadingGroup(fillDirection);
			var leadingEdge = leadingGroup?.GetLeadingEdge(fillDirection) ?? _dynamicSeedStart ?? 0;
			_dynamicSeedStart = null;
			var increment = fillDirection == FillDirection.Forward ? 1 : -1;

			int groupToCreate = leadingGroup?.GroupIndex ?? _dynamicSeedIndex?.Section ?? -1;
			//The 'seed' index may be in the same group as the target to create if we are doing a lightweight layout rebuild
			if (groupToCreate == targetGroupIndex)
			{
				groupToCreate -= increment;
			}
			if (groupToCreate / increment > targetGroupIndex / increment)
			{
				if (this.Log().IsEnabled(Microsoft.Extensions.Logging.LogLevel.Error))
				{
					this.Log().Error($"Invalid state when creating new groups: leadingGroup.GroupIndex={leadingGroup?.GroupIndex}, targetGroupIndex={targetGroupIndex}, fillDirection={fillDirection}");
				}
				return;
			}

			//Create the desired group and any intervening empty groups
			do
			{
				groupToCreate += increment;
				if (leadingGroup == null || IsThereAGapOutsideGroup(leadingGroup, fillDirection, scrollOffset, availableExtent))
				{
					CreateGroupAtLeadingEdge(groupToCreate, fillDirection, availableBreadth, recycler, state, leadingEdge);
				}
				leadingGroup = GetLeadingGroup(fillDirection);
				leadingEdge = leadingGroup.GetLeadingEdge(fillDirection);
			}
			while (groupToCreate != targetGroupIndex);
		}

		/// <summary>
		/// Add a new group to the internal state of the layout. It will be added at the end if filling forward or the start if 
		/// filling backward. If filling backward, the cached layout information of the group will be restored.
		/// </summary>
		private void CreateGroupAtLeadingEdge(int groupIndex, FillDirection fillDirection, int availableBreadth, RecyclerView.Recycler recycler, RecyclerView.State state, int trailingEdge)
		{
			var group = new Group(groupIndex);
			group.Start = trailingEdge;

			CreateGroupHeader(fillDirection, InitialBreadthPadding, availableBreadth, recycler, state, group);

			if (fillDirection == FillDirection.Forward)
			{
				_groups.AddToBack(group);
			}
			else
			{
				_groups.AddToFront(group);
			}
		}

		/// <summary>
		/// Materialize a view for a group header.
		/// </summary>
		private void CreateGroupHeader(FillDirection fillDirection, int breadthOffset, int availableBreadth, RecyclerView.Recycler recycler, RecyclerView.State state, Group group)
		{
			var displayItemIndex = GetGroupHeaderAdapterIndex(group.GroupIndex);
			var headerView = recycler.GetViewForPosition(displayItemIndex, state);
			Debug.Assert(headerView is ListViewBaseHeaderItem, "view is ListViewBaseHeaderItem (We should never be given a regular item container)");
			group.RelativeHeaderPlacement = RelativeGroupHeaderPlacement;

			AddViewAtOffset(headerView, fillDirection, group.Start, breadthOffset, availableBreadth, viewType: ViewType.GroupHeader);

			group.HeaderExtent = GetChildExtentWithMargins(headerView);
			group.HeaderBreadth = GetChildBreadthWithMargins(headerView);

			if (fillDirection == FillDirection.Back)
			{
				//If filling backward, adjust the start of the group to account for the header's extent.
				group.Start -= group.HeaderExtent;
			}

		}

		/// <summary>
		/// Materialize header and footer views, if they should be shown.
		/// </summary>
		/// <returns>The extent of the header (used for layouting).</returns>
		private int CreateHeaderAndFooter(int extentOffset, int breadthOffset, int availableBreadth, RecyclerView.Recycler recycler, RecyclerView.State state)
		{
			if (XamlParent == null)
			{
				return 0;
			}

			int headerExtent = 0;
			if (XamlParent.ShouldShowHeader)
			{
				var header = recycler.GetViewForPosition(0, state);
				AddViewAtOffset(header, FillDirection.Forward, extentOffset, breadthOffset, availableBreadth, viewType: ViewType.Header);
				headerExtent = GetChildExtentWithMargins(header);
			}

			if (XamlParent.ShouldShowFooter)
			{
				var footer = recycler.GetViewForPosition(XamlParent.ShouldShowHeader ? 1 : 0, state);
				AddViewAtOffset(footer, FillDirection.Forward, extentOffset + headerExtent, breadthOffset, availableBreadth, viewType: ViewType.Footer);
			}

			return headerExtent;
		}

		/// <summary>
		/// Dematerialize lines and group headers that are no longer visible with the nominated offset.
		/// </summary>
		private void UnfillLayout(FillDirection direction, int offset, int availableExtent, RecyclerView.Recycler recycler, RecyclerView.State state)
		{
			// Keep at least one item materialized, this permits Header and Footer to be positioned correctly.
			while (ItemViewCount > 1)
			{
				var trailingLine = GetTrailingLine(direction);
				if (IsLineVisible(direction, trailingLine, availableExtent, offset))
				{
					break;
				}
				else
				{
					RemoveTrailingLine(direction, recycler);
				}
			}

			while (GroupHeaderViewCount > 0)
			{
				var trailingGroup = GetTrailingGroup(direction);
				if (trailingGroup.Lines.Count == 0 && !IsGroupVisible(trailingGroup, availableExtent, offset))
				{
					RemoveTrailingGroup(direction, recycler);
				}
				else
				{
					break;
				}
			}

			AssertValidState();
		}

		[Conditional("DEBUG")]
		private void AssertValidState()
		{
			Debug.Assert(GroupHeaderViewCount >= 0, "GroupHeaderViewCount >= 0");
			Debug.Assert(ItemViewCount >= 0, "ItemViewCount >= 0");
			Debug.Assert(HeaderViewCount >= 0, "HeaderViewCount >= 0");
			Debug.Assert(HeaderViewCount <= 1, "HeaderViewCount <= 1");
			Debug.Assert(FooterViewCount >= 0, "FooterViewCount >= 0");
			Debug.Assert(FooterViewCount <= 1, "FooterViewCount <= 1");
			Debug.Assert(ItemViewCount + GroupHeaderViewCount + HeaderViewCount + FooterViewCount == ChildCount,
				"ItemViewCount + GroupHeaderViewCount + HeaderViewCount + FooterViewCount == ChildCount");
		}

		/// <summary>
		/// Tears down the current layout and allows it to be recreated without losing the current scroll position.
		/// </summary>
		private void ScrapLayout(RecyclerView.Recycler recycler, int availableBreadth)
		{
			var direction = FillDirection.Forward;
			var firstVisibleItem = GetTrailingLine(direction)?.FirstItem;
			//Get 'seed' information for recreating layout
			var adjustedFirstItem = GetAdjustedFirstItem(firstVisibleItem);

			var headerViewCount = HeaderViewCount;

			if (HeaderViewCount > 0 &&
				(!adjustedFirstItem.HasValue || adjustedFirstItem == IndexPath.Zero)
			)
			{
				// If the header is visible, ensure to reapply its size in case it changes. 
				_isInitialExtentOffsetApplied = false;
				_previousHeaderExtent = GetChildExtentWithMargins(GetHeaderViewIndex());
			}

			_dynamicSeedIndex = GetDynamicSeedIndex(adjustedFirstItem, availableBreadth);
			_dynamicSeedStart = GetTrailingGroup(direction)?.Start;

			// Scrapped views will be preferentially reused by RecyclerView, without rebinding if the item hasn't changed, which is 
			// much cheaper than fully recycling an item view.
			DetachAndScrapAttachedViews(recycler);

			while (ItemViewCount > 0)
			{
				RemoveTrailingLine(FillDirection.Back, recycler, detachOnly: true);
			}

			while (GroupHeaderViewCount > 0)
			{
				RemoveTrailingGroup(direction, recycler, detachOnly: true);
			}

			HeaderViewCount = 0;
			FooterViewCount = 0;
			_areHeaderAndFooterCreated = false;
		}

		/// <summary>
		/// Get 'seed' index for recreating the visual state of the list after <see cref="ScrapLayout(RecyclerView.Recycler, int)"/>;
		/// </summary>
		protected virtual IndexPath? GetDynamicSeedIndex(IndexPath? firstVisibleItem, int availableBreadth)
		{
			if (ContentOffset == 0)
			{
				// Ensure that the entire dataset is drawn if the list hasn't been scrolled. This is otherwise sometimes not done correctly 
				// if a previously-empty group becomes occupied.
				return null;
			}

			var lastItem = XamlParent.GetLastItem();
			if (lastItem == null ||
				(firstVisibleItem != null && firstVisibleItem.Value > lastItem.Value)
			)
			{
				// None of the previously-visible indices are now present in the updated items source
				return null;
			}
			return GetNextUnmaterializedItem(FillDirection.Back, firstVisibleItem);
		}

		/// <summary>
		/// Update the first visible item in case the group it occupies has changed due to INotifyCollectionChanged operations.
		/// </summary>
		private IndexPath? GetAdjustedFirstItem(IndexPath? firstItem)
		{
			if (_pendingGroupOperations.Count == 0)
			{
				return firstItem;
			}

			if (firstItem == null)
			{
				_pendingGroupOperations.Clear();
				return null;
			}

			var section = firstItem.Value.Section;
			var row = firstItem.Value.Row;

			while (_pendingGroupOperations.Count > 0)
			{
				var op = _pendingGroupOperations.Dequeue();
				if (op.Type == ListViewBase.GroupOperationType.Add)
				{
					if (op.GroupIndex <= section)
					{
						section++;
					}
				}
				if (op.Type == ListViewBase.GroupOperationType.Remove)
				{
					if (op.GroupIndex < section)
					{
						section--;
					}
					else if (op.GroupIndex == section)
					{
						// Group containing the first visible item has been deleted. Try to display the start of the next group. (If there 
						// is no next group, this will be caught later.)
						row = 0;
					}
				}
			}

			if (section < 0)
			{
				return null;
			}

			return IndexPath.FromRowSection(row, section);
		}

		/// <summary>
		/// Set header and footer dirty and trigger a layout to recreate them.
		/// </summary>
		internal void UpdateHeaderAndFooter()
		{
			_needsHeaderAndFooterUpdate = true;
			RequestLayout();
		}

		/// <summary>
		/// Rebind and recycle any existing header and footer views.
		/// </summary>
		private void ResetHeaderAndFooter(RecyclerView.Recycler recycler)
		{
			//remove existing header and footer, create, update positions
			if (HeaderViewCount > 0)
			{
				var headerIndex = GetHeaderViewIndex();
				_previousHeaderExtent = GetChildExtentWithMargins(headerIndex);
				// Rebind to apply changes, RecyclerView alone will recycle the view without rebinding.
				recycler.BindViewToPosition(GetChildAt(headerIndex), headerIndex);
				base.RemoveAndRecycleViewAt(headerIndex, recycler);
				HeaderViewCount = 0;
			}

			if (FooterViewCount > 0)
			{
				var footerIndex = GetFooterViewIndex();
				// Rebind to apply changes, RecyclerView alone will recycle the view without rebinding.
				recycler.BindViewToPosition(GetChildAt(footerIndex), footerIndex);
				base.RemoveAndRecycleViewAt(footerIndex, recycler);
				FooterViewCount = 0;
			}

			_areHeaderAndFooterCreated = false;
			_isInitialExtentOffsetApplied = false;
		}

		private IEnumerable<float> GetSnapPointsInner(SnapPointsAlignment alignment)
		{
			for (int i = 0; i < ChildCount; i++)
			{
				yield return GetSnapPoint(GetChildAt(i), alignment);
			}
		}

		private float GetSnapPoint(View view, SnapPointsAlignment alignment)
		{
			switch (alignment)
			{
				case SnapPointsAlignment.Near:
					return ContentOffset + GetChildStartWithMargin(view);
				case SnapPointsAlignment.Center:
					return ContentOffset + (GetChildStartWithMargin(view) + GetChildEndWithMargin(view)) / 2f;
				case SnapPointsAlignment.Far:
					return ContentOffset + GetChildEndWithMargin(view);
				default:
					throw new ArgumentOutOfRangeException(nameof(alignment));
			}
		}

		/// <summary>
		/// Apply snap points alignment to scroll offset.
		/// </summary>
		private float AdjustOffsetForSnapPointsAlignment(float offset)
		{

			switch (SnapPointsAlignment)
			{
				case SnapPointsAlignment.Near:
					return offset;
				case SnapPointsAlignment.Center:
					return offset + Extent / 2f;
				case SnapPointsAlignment.Far:
					return offset + Extent;
				default:
					throw new InvalidOperationException();
			}
		}

		/// <summary>
		/// Returns true if there is space between the edge of the leading item within the group and the edge of the viewport in the 
		/// desired fill direction, false otherwise.
		/// </summary>
		private bool IsThereAGapWithinGroup(Group group, FillDirection fillDirection, int offset, int availableExtent)
		{
			var leadingEdge = GetLeadingEdgeWithinGroup(group, fillDirection);
			return IsThereAGap(leadingEdge, fillDirection, offset, availableExtent);
		}

		/// <summary>
		/// Get the edge of the leading item of the group in the desired fill direction. Note that this may differ from the Start/End of 
		/// the group because if the group header is <see cref="RelativeHeaderPlacement.Adjacent"/>, it may take up more extent than the items themselves.
		/// </summary>
		private int GetLeadingEdgeWithinGroup(Group group, FillDirection fillDirection)
		{
			var leadingLine = group.GetLeadingLine(fillDirection);
			if (leadingLine == null)
			{
				return fillDirection == FillDirection.Forward ?
					group.Start + group.ItemsExtentOffset :
					group.End;
			}
			var view = GetLeadingItemView(fillDirection);
			return fillDirection == FillDirection.Forward ?
				GetChildStartWithMargin(view) + leadingLine.Extent :
				GetChildStartWithMargin(view);
		}

		/// <summary>
		/// True if there is space between the leading edge of the group and the edge of the viewport in the 
		/// desired fill direction, false otherwise.
		/// </summary>
		private bool IsThereAGapOutsideGroup(Group group, FillDirection fillDirection, int offset, int availableExtent)
		{
			var leadingEdge = fillDirection == FillDirection.Forward ?
				group.End :
				group.Start;
			return IsThereAGap(leadingEdge, fillDirection, offset, availableExtent);
		}

		/// <summary>
		/// True if there is a gap between the nominated leading edge and the edge of the viewport after the nominated scroll offset is applied,
		/// false otherwise.
		/// </summary>
		private bool IsThereAGap(int leadingEdge, FillDirection fillDirection, int offset, int availableExtent)
		{
			if (fillDirection == FillDirection.Forward)
			{
				return leadingEdge - offset < availableExtent;
			}
			else
			{
				return leadingEdge - offset > 0;
			}
		}

		/// <summary>
		/// True if the nominated line is still visible after the nominated scroll offset is applied, false otherwise.
		/// </summary>
		private bool IsLineVisible(FillDirection direction, Line line, int availableExtent, int offset)
		{
			int near = 0;

			var childStart = GetChildStartWithMargin(direction == FillDirection.Forward ? FirstItemView : FirstItemView + ItemViewCount - 1);
			// If availableExtent is set to MaxValue, halve it to avoid integer overflow
			if (availableExtent == int.MaxValue) { availableExtent /= 2; }
			return childStart < (availableExtent + offset) && (childStart + line.Extent) > (near + offset);
		}

		/// <summary>
		/// True if the nominated group is still visible after the nominated scroll offset is applied, false otherwise.
		/// </summary>
		private bool IsGroupVisible(Group group, int availableExtent, int offset)
		{
			var offsetStart = group.Start - offset;
			var offsetEnd = group.End - offset;
			return offsetStart <= availableExtent && offsetEnd >= 0;
		}

		private int GetChildStartWithMargin(int childIndex)
		{
			var child = GetChildAt(childIndex);
			if (child == null)
			{
				return 0;
			}
			return GetChildStartWithMargin(child);
		}

		private int GetChildStartWithMargin(View child)
		{
			var start = GetChildStart(child);
			int margin = 0;
			var asFrameworkElement = child as IFrameworkElement;
			if (asFrameworkElement != null)
			{
				var logicalMargin = ScrollOrientation == Orientation.Vertical ?
					asFrameworkElement.Margin.Top :
					asFrameworkElement.Margin.Left;
				margin = (int)ViewHelper.LogicalToPhysicalPixels(logicalMargin);
			}
			return start - margin;
		}

		private int GetChildStart(View child)
		{
			return ScrollOrientation == Orientation.Vertical ?
							child.Top :
							child.Left;
		}

		private int GetChildEndWithMargin(int childIndex)
		{
			var child = GetChildAt(childIndex);
			if (child == null)
			{
				return 0;
			}
			return GetChildEndWithMargin(child);
		}

		private int GetChildEndWithMargin(View child)
		{
			var end = GetChildEnd(child);
			int margin = 0;
			var asFrameworkElement = child as IFrameworkElement;
			if (asFrameworkElement != null)
			{
				var logicalMargin = ScrollOrientation == Orientation.Vertical ?
					asFrameworkElement.Margin.Bottom :
					asFrameworkElement.Margin.Right;
				margin = (int)ViewHelper.LogicalToPhysicalPixels(logicalMargin);
			}
			return end + margin;
		}

		private int GetChildEnd(View child)
		{
			return ScrollOrientation == Orientation.Vertical ?
							child.Bottom :
							child.Right;
		}

		private int GetChildExtentWithMargins(View child)
		{
			var margin = (child as IFrameworkElement)?.Margin.LogicalToPhysicalPixels() ?? Thickness.Empty;
			return ScrollOrientation == Orientation.Vertical ?
							child.Bottom - child.Top + (int)margin.Bottom + (int)margin.Top :
							child.Right - child.Left + (int)margin.Left + (int)margin.Right;
		}

		private int GetChildExtentWithMargins(int childIndex)
		{
			var child = GetChildAt(childIndex);
			return GetChildExtentWithMargins(child);
		}

		private int GetChildBreadthWithMargins(View child)
		{
			var margin = (child as IFrameworkElement)?.Margin.LogicalToPhysicalPixels() ?? Thickness.Empty;
			return ScrollOrientation == Orientation.Vertical ?
							child.Right - child.Left + (int)margin.Left + (int)margin.Right :
							child.Bottom - child.Top + (int)margin.Bottom + (int)margin.Top;
		}

		/// <summary>
		/// Return the farthest extent of all currently materialized content.
		/// </summary>
		private int GetContentEnd()
		{
			int contentEnd = GetLeadingGroup(FillDirection.Forward)?.End ?? 0;
			if (FooterViewCount > 0)
			{
				contentEnd += GetChildExtentWithMargins(GetFooterViewIndex());
			}
			contentEnd += FinalExtentPadding;
			return contentEnd;
		}

		/// <summary>
		/// Return the nearest extent of all currently materialized content.
		/// </summary>
		private int GetContentStart()
		{
			int contentStart = GetLeadingGroup(FillDirection.Back)?.Start ?? 0;
			if (HeaderViewCount > 0)
			{
				contentStart -= GetChildExtentWithMargins(GetHeaderViewIndex());
			}
			contentStart -= InitialExtentPadding;
			return contentStart;
		}

		private Group GetLeadingGroup(FillDirection fillDirection)
		{
			return fillDirection == FillDirection.Forward ?
				GetLastGroup() :
				GetFirstGroup();
		}

		private Group GetTrailingGroup(FillDirection fillDirection)
		{
			return fillDirection == FillDirection.Forward ?
				GetFirstGroup() :
				GetLastGroup();
		}

		/// <summary>
		/// Get the leading non-empty group in the nominated fill direction.
		/// </summary>
		private Group GetLeadingNonEmptyGroup(FillDirection fillDirection)
		{
			var startingValue = fillDirection == FillDirection.Forward ?
				_groups.Count - 1 : 0;
			var increment = fillDirection == FillDirection.Forward ? -1 : 1;
			for (int i = startingValue; i >= 0 && i < _groups.Count; i += increment)
			{
				var group = _groups[i];
				if (group.Lines.Count > 0)
				{
					return group;
				}
			}
			return null;
		}

		private Group GetTrailingNonEmptyGroup(FillDirection fillDirection)
		{
			var oppositeDirection = fillDirection == FillDirection.Forward ? FillDirection.Back : FillDirection.Forward;

			return GetLeadingNonEmptyGroup(oppositeDirection);
		}

		private Line GetTrailingLine(FillDirection fillDirection)
		{
			var containingGroup = GetTrailingNonEmptyGroup(fillDirection);
			return containingGroup?.GetTrailingLine(fillDirection);
		}

		private Line GetLeadingLine(FillDirection fillDirection)
		{
			var containingGroup = GetLeadingNonEmptyGroup(fillDirection);
			return containingGroup?.GetLeadingLine(fillDirection);
		}

		private IndexPath? GetNextUnmaterializedItem(FillDirection fillDirection)
		{
			return GetNextUnmaterializedItem(fillDirection, GetLeadingMaterializedItem(fillDirection));
		}

		/// <summary>
		/// Get the index of the next item that has not yet been materialized in the nominated fill direction. Returns null if there are no more available items in the source.
		/// </summary>
		protected IndexPath? GetNextUnmaterializedItem(FillDirection fillDirection, IndexPath? currentMaterializedItem)
		{
			return XamlParent?.GetNextItemIndex(currentMaterializedItem, fillDirection == FillDirection.Forward ? 1 : -1);
		}

		private View GetGroupHeaderAt(int groupHeaderIndex)
		{
			var view = GetChildAt(GetGroupHeaderViewIndex(groupHeaderIndex));
			Debug.Assert(view is ListViewBaseHeaderItem, "view is ListViewBaseHeaderItem");
			return view;
		}

		private int GetGroupHeaderViewIndex(int groupHeaderIndex)
		{
			return FirstItemView + ItemViewCount + groupHeaderIndex;
		}

		private int GetHeaderViewIndex()
		{
			if (HeaderViewCount < 1)
			{
				throw new InvalidOperationException();
			}
			return 0;
		}

		private int GetFooterViewIndex()
		{
			if (FooterViewCount < 1)
			{
				throw new InvalidOperationException();
			}
			return ChildCount - 1;
		}

		private IndexPath? GetLeadingMaterializedItem(FillDirection fillDirection)
		{
			var group = GetLeadingNonEmptyGroup(fillDirection);
			return group?.GetLeadingMaterializedItem(fillDirection);
		}

		private View GetLeadingItemView(FillDirection fillDirection)
		{
			return GetChildAt(GetLeadingItemViewIndex(fillDirection));
		}

		private int GetTrailingItemViewIndex(FillDirection fillDirection)
		{
			return fillDirection == FillDirection.Forward ?
				FirstItemView :
				FirstItemView + ItemViewCount - 1;
		}

		private int GetLeadingItemViewIndex(FillDirection fillDirection)
		{
			return fillDirection == FillDirection.Forward ?
				FirstItemView + ItemViewCount - 1 :
				FirstItemView;
		}

		/// <summary>
		/// Remove the trailing item view in the nominated fill direction and update the internal layout state.
		/// </summary>
		private void RemoveTrailingView(FillDirection fillDirection, RecyclerView.Recycler recycler, bool detachOnly)
		{
			var trailingViewIndex = GetTrailingItemViewIndex(fillDirection);
			if (!detachOnly)
			{
				ViewCache.DetachAndCacheView(GetChildAt(trailingViewIndex), recycler);
			}
			ItemViewCount--;
		}

		/// <summary>
		/// Remove the trailing line in the nominated fill direction and update the internal layout state.
		/// </summary>
		private void RemoveTrailingLine(FillDirection fillDirection, RecyclerView.Recycler recycler, bool detachOnly = false)
		{
			var containingGroup = GetTrailingNonEmptyGroup(fillDirection);
			var line = containingGroup.GetTrailingLine(fillDirection);
			for (int i = 0; i < line.NumberOfViews; i++)
			{
				RemoveTrailingView(fillDirection, recycler, detachOnly);
			}
			containingGroup.RemoveTrailingLine(fillDirection);
		}

		/// <summary>
		/// Remove the trailing group in the nominated fill direction and update the internal layout state.
		/// </summary>
		private void RemoveTrailingGroup(FillDirection fillDirection, RecyclerView.Recycler recycler, bool detachOnly = false)
		{
			Debug.Assert(GetTrailingGroup(fillDirection).Lines.Count == 0, "No lines remaining in group being removed");

			if (!detachOnly)
			{
				ViewCache.DetachAndCacheView(GetChildAt(GetGroupHeaderViewIndex(fillDirection == FillDirection.Forward ? 0 : GroupHeaderViewCount - 1)), recycler);
			}
			GroupHeaderViewCount--;

			if (fillDirection == FillDirection.Forward)
			{
				var group = GetFirstGroup();
				_groups.RemoveFromFront();
			}
			else
			{
				_groups.RemoveFromBack();
			}
		}

		/// <summary>
		/// Flatten item index to pass it to the native recycler.
		/// </summary>
		protected int GetFlatItemIndex(IndexPath indexPath)
		{
			return XamlParent.GetDisplayIndexFromIndexPath(indexPath);
		}

		/// <summary>
		/// Get index for header to pass to native recycler
		/// </summary>
		private int GetGroupHeaderAdapterIndex(int section)
		{
			return XamlParent.GetGroupHeaderDisplayIndex(section);
		}

		private Group GetFirstGroup()
		{
			if (_groups.Count == 0) { return null; }
			return _groups[0];
		}

		private Group GetLastGroup()
		{
			if (_groups.Count == 0) { return null; }
			return _groups[_groups.Count - 1];
		}

		internal int GetFirstVisibleDisplayPosition()
		{
			return GetFlatItemIndex(GetFirstVisibleIndexPath());
		}

		private IndexPath GetFirstVisibleIndexPath()
		{
			return GetTrailingNonEmptyGroup(FillDirection.Forward)?.GetTrailingMaterializedItem(FillDirection.Forward) ?? IndexPath.FromRowSection(-1, 0);
		}

		internal int GetLastVisibleDisplayPosition()
		{
			return GetFlatItemIndex(GetLastVisibleIndexPath());
		}

		private IndexPath GetLastVisibleIndexPath()
		{
			return GetLeadingNonEmptyGroup(FillDirection.Forward)?.GetLeadingMaterializedItem(FillDirection.Forward) ?? IndexPath.FromRowSection(-1, 0);
		}

		/// <summary>
		/// Format a message to pass to Debug.Assert.
		/// </summary>
		protected string GetAssertMessage(string message = "", [CallerMemberName] string name = null, [CallerLineNumber] int lineNumber = 0)
		{
			return message + $" - {name}, line {lineNumber}";
		}
	}
}