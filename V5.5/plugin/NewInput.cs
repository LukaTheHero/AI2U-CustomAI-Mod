using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace AI2UCustomAI
{
    // Keyboard polling through Unity's new Input System.
    //
    // Kept in its own file so the reference to Unity.InputSystem stays isolated:
    // if a future game build drops that assembly, only this type fails to load
    // and the legacy path still works.
    public static class NewInput
    {
        public static bool WasPressed(KeyCode code)
        {
            Keyboard kb = Keyboard.current;
            if (kb == null) return false;

            Key k;
            if (!TryMap(code, out k)) return false;

            KeyControl ctrl = kb[k];
            return ctrl != null && ctrl.wasPressedThisFrame;
        }

        // KeyCode and Key share most names (F8, A, Space, ...), so matching by
        // name covers nearly everything without a hand-written table.
        static bool TryMap(KeyCode code, out Key key)
        {
            key = Key.None;
            string name = code.ToString();

            try
            {
                key = (Key)Enum.Parse(typeof(Key), name, true);
                return key != Key.None;
            }
            catch (Exception)
            {
            }

            // The handful whose names differ between the two enums.
            switch (code)
            {
                case KeyCode.Return:       key = Key.Enter;         return true;
                case KeyCode.KeypadEnter:  key = Key.NumpadEnter;   return true;
                case KeyCode.LeftControl:  key = Key.LeftCtrl;      return true;
                case KeyCode.RightControl: key = Key.RightCtrl;     return true;
                case KeyCode.CapsLock:     key = Key.CapsLock;      return true;
                case KeyCode.Print:        key = Key.PrintScreen;   return true;
                case KeyCode.BackQuote:    key = Key.Backquote;     return true;
                case KeyCode.Alpha0:       key = Key.Digit0;        return true;
                case KeyCode.Alpha1:       key = Key.Digit1;        return true;
                case KeyCode.Alpha2:       key = Key.Digit2;        return true;
                case KeyCode.Alpha3:       key = Key.Digit3;        return true;
                case KeyCode.Alpha4:       key = Key.Digit4;        return true;
                case KeyCode.Alpha5:       key = Key.Digit5;        return true;
                case KeyCode.Alpha6:       key = Key.Digit6;        return true;
                case KeyCode.Alpha7:       key = Key.Digit7;        return true;
                case KeyCode.Alpha8:       key = Key.Digit8;        return true;
                case KeyCode.Alpha9:       key = Key.Digit9;        return true;
            }
            return false;
        }
    }
}
