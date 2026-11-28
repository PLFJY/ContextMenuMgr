namespace ContextMenuMgr.Contracts;

public static class RuntimeHosting
{
    public static bool IsInProcess
    {
        get
        {
#if CONTEXTMENUMGR_IN_PROCESS
            return true;
#else
            return false;
#endif
        }
    }
}
