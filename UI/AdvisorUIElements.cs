using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.GameContent.UI.Elements;
using Terraria.ID;
using Terraria.Localization;
using Terraria.UI;
using BossBuildAdvisor.Core;

namespace BossBuildAdvisor.UI;

public sealed class AdvisorButton : UIPanel
{
	private readonly UIText _label;
	private readonly Action _onClick;
	private readonly Color _baseColor;
	private readonly Color _activeColor;
	private bool _isActive;

	public AdvisorButton(string text, Action onClick, float textScale = 0.78f, Color? baseColor = null, Color? activeColor = null)
	{
		_onClick = onClick;
		_baseColor = baseColor ?? new Color(43, 55, 92, 220);
		_activeColor = activeColor ?? new Color(84, 118, 178, 240);
		BackgroundColor = _baseColor;
		BorderColor = new Color(20, 26, 46, 230);
		SetPadding(4f);

		_label = new UIText(text, textScale)
		{
			HAlign = 0.5f,
			VAlign = 0.5f
		};
		Append(_label);
	}

	public void SetText(string text) => _label.SetText(text);

	public void SetActive(bool active)
	{
		_isActive = active;
		BackgroundColor = active ? _activeColor : _baseColor;
		BorderColor = active ? new Color(245, 210, 95) : new Color(20, 26, 46, 230);
	}

	public override void MouseOver(UIMouseEvent evt)
	{
		base.MouseOver(evt);
		SoundEngine.PlaySound(SoundID.MenuTick);
		BackgroundColor = Color.Lerp(_isActive ? _activeColor : _baseColor, Color.White, 0.16f);
	}

	public override void MouseOut(UIMouseEvent evt)
	{
		base.MouseOut(evt);
		BackgroundColor = _isActive ? _activeColor : _baseColor;
	}

	public override void LeftClick(UIMouseEvent evt)
	{
		base.LeftClick(evt);
		SoundEngine.PlaySound(SoundID.MenuTick);
		_onClick?.Invoke();
	}
}

public sealed class AdvisorItemSlot : UIElement
{
	private readonly RecommendedSlotItem _slotData;
	private readonly Action<RecommendedSlotItem> _onSelect;
	private bool _selected;
	private readonly string _shortTag;
	private readonly bool _isAlternative;

	public RecommendedSlotItem SlotData => _slotData;

	public AdvisorItemSlot(RecommendedSlotItem slotData, string shortTag, float size, Action<RecommendedSlotItem> onSelect, bool isAlternative = false)
	{
		_slotData = slotData;
		_shortTag = shortTag ?? string.Empty;
		_onSelect = onSelect;
		_isAlternative = isAlternative;
		Width.Set(size, 0f);
		Height.Set(size, 0f);
	}

	public void SetSelected(bool selected) => _selected = selected;

	public override void LeftClick(UIMouseEvent evt)
	{
		base.LeftClick(evt);
		if (_slotData != null)
		{
			SoundEngine.PlaySound(SoundID.MenuTick);
			_onSelect?.Invoke(_slotData);
		}
	}

	protected override void DrawSelf(SpriteBatch spriteBatch)
	{
		base.DrawSelf(spriteBatch);

		CalculatedStyle dims = GetDimensions();
		Texture2D backTex = TextureAssets.InventoryBack9.Value;
		Color bgTint = _selected
			? new Color(255, 230, 140)
			: (_isAlternative
				? new Color(170, 192, 220, 215)
				: (_slotData?.Confidence == SourceConfidence.TierEstimated
					? new Color(225, 210, 190)
					: Color.White));

		spriteBatch.Draw(backTex, dims.ToRectangle(), bgTint);

		if (_slotData == null || _slotData.ItemType <= ItemID.None)
			return;

		if (!ContentSamples.ItemsByType.TryGetValue(_slotData.ItemType, out Item sample) || sample.IsAir)
			return;

		Main.instance.LoadItem(sample.type);
		Vector2 center = new Vector2(dims.X + dims.Width * 0.5f, dims.Y + dims.Height * 0.5f);
		float maxIcon = dims.Width * 0.68f;
		ItemSlot.DrawItemIcon(sample, ItemSlot.Context.InventoryItem, spriteBatch, center, 1f, maxIcon, Color.White);

		if (!string.IsNullOrEmpty(_shortTag))
		{
			Utils.DrawBorderStringFourWay(
				spriteBatch,
				FontAssets.ItemStack.Value,
				_shortTag,
				dims.X + 4f,
				dims.Y + dims.Height - 15f,
				_isAlternative ? new Color(165, 225, 255) : Color.LightGoldenrodYellow,
				Color.Black,
				Vector2.Zero,
				0.62f);
		}

		// Top-right obtainability badge dot (Orange = Hard, Red = VeryHard)
		if (_slotData.Difficulty >= ObtainabilityDifficulty.Hard)
		{
			Texture2D pixel = TextureAssets.MagicPixel.Value;
			Color pipColor = _slotData.Difficulty == ObtainabilityDifficulty.VeryHard
				? new Color(255, 85, 85)
				: new Color(255, 180, 65);
			var borderRect = new Rectangle((int)(dims.X + dims.Width - 10f), (int)(dims.Y + 4f), 6, 6);
			var fillRect = new Rectangle(borderRect.X + 1, borderRect.Y + 1, 4, 4);
			spriteBatch.Draw(pixel, borderRect, Color.Black);
			spriteBatch.Draw(pixel, fillRect, pipColor);
		}

		if (IsMouseHovering)
		{
			Main.HoverItem = sample.Clone();
			Main.hoverItemName = sample.Name;
		}
	}
}

public sealed class BossStageRow : UIPanel
{
	public BossStageInfo Stage { get; }
	private readonly Action<BossStageInfo> _onSelect;
	private bool _isSelected;
	private bool _isNextTarget;

	public BossStageRow(BossStageInfo stage, bool isNextTarget, Action<BossStageInfo> onSelect)
	{
		Stage = stage;
		_isNextTarget = isNextTarget;
		_onSelect = onSelect;

		Width.Set(0f, 1f);
		Height.Set(42f, 0f);
		SetPadding(5f);

		bool downed = stage.IsDowned;
		string statusMark = downed ? "[v]" : (isNextTarget ? "[>]" : "[ ]");
		Color titleColor = downed
			? new Color(135, 235, 145)
			: (isNextTarget ? new Color(255, 220, 95) : Color.White);

		string nameText = $"{statusMark} {stage.DisplayName}";
		if (nameText.Length > 28)
			nameText = nameText[..27] + "…";

		var title = new UIText(nameText, 0.78f)
		{
			Top = StyleDimension.FromPixels(1f),
			Left = StyleDimension.FromPixels(3f),
			TextColor = titleColor
		};
		Append(title);

		string tag = stage.IsBoss ? stage.ModSource : $"{stage.ModSource} • {(stage.IsEvent ? "Event" : "Mini")}";
		var sub = new UIText($"{tag} ({stage.Progression:0.#})", 0.64f)
		{
			Top = StyleDimension.FromPixels(19f),
			Left = StyleDimension.FromPixels(26f),
			TextColor = new Color(175, 190, 220)
		};
		Append(sub);

		ApplyBg();
	}

	public void SetSelected(bool selected)
	{
		_isSelected = selected;
		ApplyBg();
	}

	private void ApplyBg()
	{
		if (_isSelected)
		{
			BackgroundColor = new Color(82, 108, 165, 240);
			BorderColor = new Color(255, 215, 95);
		}
		else if (_isNextTarget)
		{
			BackgroundColor = new Color(56, 68, 106, 220);
			BorderColor = new Color(185, 160, 75, 200);
		}
		else
		{
			BackgroundColor = new Color(34, 44, 72, 210);
			BorderColor = new Color(18, 22, 38, 220);
		}
	}

	public override void LeftClick(UIMouseEvent evt)
	{
		base.LeftClick(evt);
		SoundEngine.PlaySound(SoundID.MenuTick);
		_onSelect?.Invoke(Stage);
	}
}

public sealed class BossSearchBox : UIPanel
{
	private bool _focused;
	private string _text = string.Empty;
	private readonly string _placeholder;
	private readonly Action<string> _onChanged;
	private readonly UIText _display;

	public BossSearchBox(string placeholder, Action<string> onChanged)
	{
		_placeholder = placeholder;
		_onChanged = onChanged;
		BackgroundColor = new Color(24, 31, 52, 235);
		BorderColor = new Color(60, 76, 120);
		SetPadding(5f);

		_display = new UIText(placeholder, 0.78f)
		{
			VAlign = 0.5f,
			Left = StyleDimension.FromPixels(4f),
			TextColor = new Color(150, 165, 195)
		};
		Append(_display);
	}

	public bool IsFocused => _focused;

	public void ClearFocus()
	{
		if (_focused)
		{
			_focused = false;
			if (ReferenceEquals(Main.CurrentInputTextTakerOverride, this))
				Main.CurrentInputTextTakerOverride = null;
			BorderColor = new Color(60, 76, 120);
		}
	}

	public void Focus()
	{
		if (!_focused)
		{
			_focused = true;
			Main.clrInput();
			BorderColor = new Color(245, 210, 95);
		}
		Terraria.GameInput.PlayerInput.WritingText = true;
		Main.CurrentInputTextTakerOverride = this;
	}

	public override void LeftClick(UIMouseEvent evt)
	{
		base.LeftClick(evt);
		Focus();
		SoundEngine.PlaySound(SoundID.MenuTick);
	}

	public override void Update(GameTime gameTime)
	{
		base.Update(gameTime);

		if (_focused && Main.mouseLeft && !IsMouseHovering)
			ClearFocus();

		if (_focused)
		{
			Terraria.GameInput.PlayerInput.WritingText = true;
			Main.CurrentInputTextTakerOverride = this;
		}

		bool blinkingOn = _focused && ((int)(Main.GlobalTimeWrappedHourly * 2.5f) % 2 == 0);
		string shown = string.IsNullOrEmpty(_text)
			? (_focused ? (blinkingOn ? "|" : " ") : _placeholder)
			: (_focused ? _text + (blinkingOn ? "|" : "") : _text);

		_display.SetText(shown);
		_display.TextColor = string.IsNullOrEmpty(_text) && !_focused ? new Color(150, 165, 195) : Color.White;
	}

	protected override void DrawSelf(SpriteBatch spriteBatch)
	{
		base.DrawSelf(spriteBatch);

		if (!_focused)
			return;

		Terraria.GameInput.PlayerInput.WritingText = true;
		Main.CurrentInputTextTakerOverride = this;
		Main.instance.HandleIME();

		string updated = Main.GetInputText(_text);
		if (updated != _text)
		{
			_text = updated;
			_onChanged?.Invoke(_text);
		}

		if (Main.inputTextEnter || Main.inputTextEscape)
		{
			ClearFocus();
			SoundEngine.PlaySound(SoundID.MenuTick);
		}
	}
}
