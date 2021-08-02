# Neo-Express for N3 Settings Reference

The `.neo-express` file for Neo N3 compatible versions of N3 includes a `settings` object property. 
This document details the values that Neo-Express reads from the `settings` object.

## `rpc.BindAddress`

The `rpc.BindAddress` Neo-Express setting coresponds to the `BindAddress`
[RpcServer config property](https://github.com/neo-project/neo-modules/blob/master/src/RpcServer/RpcServer/config.json#L6).

By default, Neo-Express only listens for JSON-RPC requests on the loopback address. This means that
JSON-RPC requests must originate on the same machine as Neo-Express is running in order to be serviced.
While this is the most secure approach, it limits the ability of the developer to test cross machine
scenarios, especially ones that involve mobile devices. The `rpc.BindAddress` setting can be used to
overide the default behavior.

The `rpc.BindAddress` field accepts an IP address in dotted quad notation. It specifies the IP Address
that the JSON-RPC server will listen on for client requests. Typically, to enable remote access to
a Neo-Express instance, you would specify the `rpc.BindAddress` to be `0.0.0.0`. 

If you specify an invalid IP Address, Neo-Express reverts to the default loopback `BindAddress`
(aka `127.0.0.1`).

Example usage:

``` json
  "settings": {
    "rpc.BindAddress": "0.0.0.0" // listens for JSON-RPC requests on all network interfaces
  }
```

## `rpc.MaxFee`

The `rpc.MaxFee` Neo-Express setting coresponds to the `MaxFee`
[RpcServer config property](https://github.com/neo-project/neo-modules/blob/master/src/RpcServer/RpcServer/config.json#L14).
This setting specifies a maximum Network Fee for the
[`sendfrom`](https://docs.neo.org/docs/en-us/reference/rpc/latest-version/api/sendfrom.html),
[`sendmany`](https://docs.neo.org/docs/en-us/reference/rpc/latest-version/api/sendmany.html)
and [`sendtoaddress`](https://docs.neo.org/docs/en-us/reference/rpc/latest-version/api/sendtoaddress.html)
JSON-RPC methods. 

This setting defaults to 0.1 GAS. If you specify an invalid decimal value for this setting, Neo-Express reverts to the default.

Example usage:

``` json
  "settings": {
    "rpc.MaxFee": "0.2" // support higher network fee for send[from/many/toaddress] methods
  }
```

## `rpc.MaxGasInvoke`

The `rpc.MaxGasInvoke` Neo-Express setting coresponds to the `MaxGasInvoke`
[RpcServer config property](https://github.com/neo-project/neo-modules/blob/master/src/RpcServer/RpcServer/config.json#L13).
This setting specifies maximum limit in GAS for the
[invokefunction](https://docs.neo.org/docs/en-us/reference/rpc/latest-version/api/invokefunction.html)
and [invokescript](https://docs.neo.org/docs/en-us/reference/rpc/latest-version/api/invokescript.html)
JSON-RPC methods.

This setting defaults to 10.0 GAS. If you specify an invalid decimal value for this setting, Neo-Express reverts to the default.

Example usage:

``` json
  "settings": {
    "rpc.MaxGasInvoke": "15" // support higher GAS limit for invoke[function/script] methods
  }
```

