// Input types shared across Core and Runtime
// needs expansion and could probably be integrated with another file

namespace Keystone.Core;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Shift = 1 << 0,
    Control = 1 << 1,
    Alt = 1 << 2,
    Command = 1 << 3
}
