using System.Windows.Forms;

namespace ChildSessionDesktop
{
    internal sealed class ChildSessionControl : AxHost
    {
        internal ChildSessionControl() : base("8b918b82-7985-4c24-89df-c33ad2bbfbcd") { }
        internal object GetOcxObject() { return GetOcx(); }
    }
}
