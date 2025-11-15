namespace Client;

internal enum FsmState
{
    INIT,
    PARSE_ARGS,
    // listener for server's callback
    START_CALLBACK,
    // send ClientRegister, then close
    REGISTER_WITH_SERVER,    
    POLL,     
    // accept server's callback
    ACCEPT_BACK,   
    // read & process JSON lines
    READ_AND_PROCESS,
    // crack the assigned slice
    CRACK,             
    END_PROGRAM,
    ERROR
}