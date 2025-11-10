namespace Client;

internal enum FsmState
{
    INIT,
    PARSE_ARGS,
    START_CALLBACK,          // start local listener for server's callback
    REGISTER_WITH_SERVER,    // connect to server: send ClientRegister, then close
    POLL,                    // poll callback socket(s)
    ACCEPT_BACK,             // accept server's callback
    READ_READY,              // read & process JSON lines
    RUN_ASSIGN,              // crack the assigned slice (multithreaded)
    END,
    ERROR
}