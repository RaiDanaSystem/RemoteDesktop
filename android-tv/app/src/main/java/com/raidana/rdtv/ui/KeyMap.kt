package com.raidana.rdtv.ui

import android.view.KeyEvent
import com.raidana.rdtv.proto.VK

/** Android key codes → Windows virtual-key codes for hardware keyboards. */
object KeyMap {
    fun toVk(code: Int): Int? = when (code) {
        KeyEvent.KEYCODE_DEL -> VK.BACK
        KeyEvent.KEYCODE_TAB -> VK.TAB
        KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER -> VK.ENTER
        KeyEvent.KEYCODE_ESCAPE -> VK.ESC
        KeyEvent.KEYCODE_SPACE -> VK.SPACE
        KeyEvent.KEYCODE_DPAD_LEFT -> VK.LEFT
        KeyEvent.KEYCODE_DPAD_UP -> VK.UP
        KeyEvent.KEYCODE_DPAD_RIGHT -> VK.RIGHT
        KeyEvent.KEYCODE_DPAD_DOWN -> VK.DOWN
        KeyEvent.KEYCODE_MOVE_HOME -> VK.HOME
        KeyEvent.KEYCODE_MOVE_END -> VK.END
        KeyEvent.KEYCODE_PAGE_UP -> VK.PGUP
        KeyEvent.KEYCODE_PAGE_DOWN -> VK.PGDN
        KeyEvent.KEYCODE_FORWARD_DEL -> VK.DELETE
        KeyEvent.KEYCODE_INSERT -> VK.INSERT
        KeyEvent.KEYCODE_SHIFT_LEFT, KeyEvent.KEYCODE_SHIFT_RIGHT -> VK.SHIFT
        KeyEvent.KEYCODE_CTRL_LEFT, KeyEvent.KEYCODE_CTRL_RIGHT -> VK.CTRL
        KeyEvent.KEYCODE_ALT_LEFT, KeyEvent.KEYCODE_ALT_RIGHT -> VK.ALT
        KeyEvent.KEYCODE_META_LEFT, KeyEvent.KEYCODE_META_RIGHT -> VK.LWIN
        in KeyEvent.KEYCODE_F1..KeyEvent.KEYCODE_F12 -> VK.F1 + (code - KeyEvent.KEYCODE_F1)
        in KeyEvent.KEYCODE_A..KeyEvent.KEYCODE_Z -> 'A'.code + (code - KeyEvent.KEYCODE_A)
        in KeyEvent.KEYCODE_0..KeyEvent.KEYCODE_9 -> '0'.code + (code - KeyEvent.KEYCODE_0)
        else -> null
    }

    /** Keys that must go as virtual keys (not as typed characters) even without Ctrl/Alt. */
    fun isNonText(vk: Int) = vk !in 0x30..0x5A && vk != VK.SPACE
}
