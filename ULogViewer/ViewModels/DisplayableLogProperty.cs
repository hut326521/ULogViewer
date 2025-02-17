﻿using CarinaStudio.ULogViewer.Logs;
using CarinaStudio.ULogViewer.Logs.Profiles;
using System;
using System.ComponentModel;

namespace CarinaStudio.ULogViewer.ViewModels
{
	/// <summary>
	/// Property of <see cref="DisplayableLog"/> to be shown on UI.
	/// </summary>
	class DisplayableLogProperty : BaseDisposable, INotifyPropertyChanged
	{
		// Fields.
		readonly IApplication app;
		readonly string displayNameId;


		/// <summary>
		/// Initialize new <see cref="DisplayableLogProperty"/> instance.
		/// </summary>
		/// <param name="app">Application.</param>
		/// <param name="name">Name of property.</param>
		/// <param name="displayName">Name for displaying.</param>
		/// <param name="width">Width of UI field to show property in pixels.</param>
		public DisplayableLogProperty(IApplication app, string name, string? displayName, int? width)
		{
			this.app = app;
			this.displayNameId = displayName ?? name;
			this.DisplayName = app.GetStringNonNull($"DisplayableLogProperty.{this.displayNameId}", this.displayNameId);
			this.Name = name;
			this.Width = width;
			app.StringsUpdated += this.OnApplicationStringsUpdated;
		}


		/// <summary>
		/// Initialize new <see cref="DisplayableLogProperty"/> instance.
		/// </summary>
		/// <param name="app">Application.</param>
		/// <param name="logProperty"><see cref="LogProperty"/> defined in <see cref="LogProfile"/>.</param>
		public DisplayableLogProperty(IApplication app, LogProperty logProperty)
		{
			this.app = app;
			this.displayNameId = logProperty.DisplayName;
			this.Name = logProperty.Name switch
			{
				nameof(Log.Timestamp) => nameof(DisplayableLog.TimestampString),
				_ => logProperty.Name,
			};
			this.DisplayName = app.GetStringNonNull($"DisplayableLogProperty.{this.displayNameId}", this.displayNameId);
			this.Width = logProperty.Width;
			app.StringsUpdated += this.OnApplicationStringsUpdated;
		}


		/// <summary>
		/// Get name for displaying.
		/// </summary>
		public string DisplayName { get; private set; }


		// Dispose.
		protected override void Dispose(bool disposing)
		{
			this.app.StringsUpdated -= this.OnApplicationStringsUpdated;
		}


		/// <summary>
		/// Name of property.
		/// </summary>
		public string Name { get; }


		// Called when application strings updated.
		void OnApplicationStringsUpdated(object? sender, EventArgs e)
		{
			var newDisplayName = this.app.GetStringNonNull($"DisplayableLogProperty.{this.displayNameId}", this.displayNameId);
			if (this.DisplayName == newDisplayName)
				return;
			this.DisplayName = newDisplayName;
			this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
		}


		/// <summary>
		/// Raised when property changed.
		/// </summary>
		public event PropertyChangedEventHandler? PropertyChanged;


		/// <summary>
		/// Width of UI field to show property in pixels.
		/// </summary>
		public int? Width { get; }
	}
}
