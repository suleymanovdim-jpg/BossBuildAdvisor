using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.UI;

namespace BossBuildAdvisor.UI;

/// <summary>
/// Inventory button that toggles the Boss Build Advisor window without colliding with BossChecklist's log book.
/// Uses tModLoader's standard UIElement hover/click lifecycle in UpdateUI so mouseInterface is claimed before World/Player updates.
/// </summary>
public sealed class AdvisorInventoryButton : UIElement
{
	public const int ButtonWidth = 32;
	public const int ButtonHeight = 34;

	private readonly Func<Vector2> _getBossChecklistPos;
	private readonly Func<Asset<Texture2D>> _getTexture;
	private readonly Func<bool> _isOpen;
	private readonly Func<bool> _canInteract;
	private readonly Func<string> _getTooltip;
	private readonly Action _onToggle;

	private float _lastLeft = float.NaN;
	private float _lastTop = float.NaN;

	public AdvisorInventoryButton(
		Func<Vector2> getBossChecklistPos,
		Func<Asset<Texture2D>> getTexture,
		Func<bool> isOpen,
		Func<bool> canInteract,
		Func<string> getTooltip,
		Action onToggle)
	{
		_getBossChecklistPos = getBossChecklistPos;
		_getTexture = getTexture;
		_isOpen = isOpen;
		_canInteract = canInteract;
		_getTooltip = getTooltip;
		_onToggle = onToggle;

		Width.Set(ButtonWidth, 0f);
		Height.Set(ButtonHeight, 0f);
		OnLeftClick += HandleLeftClick;
	}

	public void UpdatePlacement()
	{
		Vector2 bcPos = _getBossChecklistPos();
		const int gap = 8;

		// BossChecklist stores BossLogPos as the TOP-LEFT offset from (screenWidth, screenHeight) for its 34x38 icon.
		float bookX = Math.Clamp(Main.screenWidth + bcPos.X, 0f, Main.screenWidth - 34f);
		float bookY = Math.Clamp(Main.screenHeight + bcPos.Y, 0f, Main.screenHeight - 38f);
		int y = (int)Math.Clamp(bookY + (38f - ButtonHeight) * 0.5f, 8f, Main.screenHeight - ButtonHeight - 8f);

		float leftX = bookX - gap - ButtonWidth;
		float rightX = bookX + 34f + gap;
		bool chestOrShop = Main.LocalPlayer != null && (Main.LocalPlayer.chest != -1 || Main.npcShop > 0);
		bool leftBlocked = leftX < 8f || (chestOrShop && leftX < 520f && y > 235f && y < 480f);
		float chosenX = leftBlocked && rightX + ButtonWidth <= Main.screenWidth - 8f ? rightX : leftX;
		int x = (int)Math.Clamp(chosenX, 8f, Main.screenWidth - ButtonWidth - 8f);

		if (float.IsNaN(_lastLeft) || float.IsNaN(_lastTop)
			|| MathF.Abs(_lastLeft - x) > 0.1f || MathF.Abs(_lastTop - y) > 0.1f)
		{
			_lastLeft = x;
			_lastTop = y;
			Left.Set(x, 0f);
			Top.Set(y, 0f);
			(Parent ?? this).Recalculate();
		}
	}

	public override void MouseOver(UIMouseEvent evt)
	{
		base.MouseOver(evt);
		if (_canInteract())
			SoundEngine.PlaySound(SoundID.MenuTick);
	}

	private void HandleLeftClick(UIMouseEvent evt, UIElement listeningElement)
	{
		if (!_canInteract())
			return;

		Main.mouseLeftRelease = false;
		SoundEngine.PlaySound(_isOpen() ? SoundID.MenuClose : SoundID.MenuOpen);
		_onToggle();
	}

	protected override void DrawSelf(SpriteBatch spriteBatch)
	{
		base.DrawSelf(spriteBatch);

		Rectangle rect = GetDimensions().ToRectangle();
		bool open = _isOpen();
		bool hovered = IsMouseHovering && _canInteract();

		Asset<Texture2D> texture = _getTexture();
		if (texture != null && texture.IsLoaded)
		{
			float alpha = hovered || open ? 1f : 0.78f;
			spriteBatch.Draw(texture.Value, rect, Color.White * alpha);
		}
		else
		{
			Color bg = open
				? new Color(75, 112, 175, 235)
				: (hovered ? new Color(62, 78, 122, 235) : new Color(34, 45, 76, 215));
			Utils.DrawInvBG(spriteBatch, rect, bg);
		}

		if (hovered || open)
		{
			Texture2D pixel = TextureAssets.MagicPixel.Value;
			Color border = open ? new Color(110, 220, 255) : new Color(255, 215, 90);
			spriteBatch.Draw(pixel, new Rectangle(rect.X - 1, rect.Y - 1, rect.Width + 2, 2), border);
			spriteBatch.Draw(pixel, new Rectangle(rect.X - 1, rect.Bottom - 1, rect.Width + 2, 2), border);
			spriteBatch.Draw(pixel, new Rectangle(rect.X - 1, rect.Y - 1, 2, rect.Height + 2), border);
			spriteBatch.Draw(pixel, new Rectangle(rect.Right - 1, rect.Y - 1, 2, rect.Height + 2), border);
		}

		if (hovered)
		{
			if (Main.LocalPlayer != null)
				Main.LocalPlayer.mouseInterface = true;
			Main.blockMouse = true;
			Main.HoverItem.TurnToAir();
			Main.rare = ItemRarityID.White;
			Main.hoverItemName = _getTooltip();
		}
	}
}

/// <summary>
/// Dedicated UIState hosting <see cref="AdvisorInventoryButton"/> while the player's inventory is visible.
/// </summary>
public sealed class AdvisorButtonUIState : UIState
{
	private readonly AdvisorInventoryButton _button;
	private readonly Func<bool> _canInteract;

	public AdvisorInventoryButton Button => _button;

	public AdvisorButtonUIState(
		Func<Vector2> getBossChecklistPos,
		Func<Asset<Texture2D>> getTexture,
		Func<bool> isOpen,
		Func<bool> canInteract,
		Func<string> getTooltip,
		Action onToggle)
	{
		_canInteract = canInteract;
		_button = new AdvisorInventoryButton(getBossChecklistPos, getTexture, isOpen, canInteract, getTooltip, onToggle);
	}

	public override void OnInitialize()
	{
		Append(_button);
	}

	public void UpdatePlacement()
	{
		_button.UpdatePlacement();
	}

	public override void Update(GameTime gameTime)
	{
		_button.UpdatePlacement();
		base.Update(gameTime);

		if (_button.IsMouseHovering && _canInteract())
		{
			if (Main.LocalPlayer != null)
				Main.LocalPlayer.mouseInterface = true;
			Main.blockMouse = true;
		}
	}
}
