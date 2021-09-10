using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text;

using UnityEngine;
using UnityEngine.Networking;


public class RPCOptions
{
    public string host;
    public string port;
    public string key;
    public int timeout = 5;
    public string verify = "0";

    public RPCOptions(string host, string port, string key)
    {
        this.host = host;
        this.port = port;
        this.key = key;
    }
    public RPCOptions(string host, string port, string key, int timeout, string verify)
    {
        this.host = host;
        this.port = port;
        this.key = key;
        this.timeout = timeout;
        this.verify = verify;
    }
}

public class RPCValue
{
    public string name;
    public dynamic value;
    public RPCValue(string argName, dynamic val)
    {
        this.name = argName;
        this.value = val;
    }
}

public class RPCCallback
{
    public string name;
    public dynamic value;
    public RPCCallback(string argName, Func<dynamic, dynamic> callback)
    {
        this.name = argName;
        this.value = callback;
    }

    public RPCCallback(string argName, Func<dynamic, Task<dynamic>> callback)
    {
        this.name = argName;
        this.value = callback;
    }
}

[Serializable]
public class CallbackResponse
{
    public string m;
    public string t;
    public string kid;
}

public class RPCMultiTyped
{
    private string rawValue;
    private RPCOptions options;
    private ReachRPC reachRPC;

    public RPCMultiTyped(string value, RPCOptions opts)
    {
        this.rawValue = value;
        this.options = opts;
        this.reachRPC = new ReachRPC(opts);
    }

    public string AsString()
    {
        return rawValue;
    }

    public int AsInt()
    {
        return Int32.Parse(rawValue);
    }

    public bool AsBool()
    {
        return Boolean.Parse(rawValue);
    }

    // TODO: As Data
    // TODO: As Object

    public RPCMultiTyped[] AsArray()
    {
        string[] r1 = RPCUtils.ExtractArray(rawValue);
        RPCMultiTyped[] r2 = new RPCMultiTyped[r1.Length];

        for (int i = 0; i < r2.Length; i++)
        {
            r2[i] = new RPCMultiTyped(r1[i], options);
        }

        return r2;
    }

    async public Task<string> AsFormattedCurrency(int precision)
    {
        return await reachRPC.CallAsync("/stdlib/formatCurrency", rawValue, precision);
    }
}

public class ReachRPC
{
    private RPCOptions options;

    public ReachRPC(RPCOptions opts)
    {
        options = opts;
    }

    public async Task<string> CallAsync(string path, params object[] args)
    {
        string url = "https://" + options.host + ":" + options.port + path;
        string postData = args.Length > 0 ? StringifyArgs(args) : "";

        Debug.Log("POST to " + url + " with data " + postData);

        // Create the request
        UnityWebRequest request = new UnityWebRequest(url, "POST");

        // Set the cerficate to accept all certificates
        if (options.verify == "0")
            request.certificateHandler = new BypassCertificate();

        // Set headers
        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("X-API-Key", options.key);
        request.SetRequestHeader("cache-control", "no-cache");

        if (postData.Length > 2)
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(postData); // Turn req body into raw data

            // Set upload and download handlers
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
        }
        else
        {
            request.downloadHandler = new DownloadHandlerBuffer();
        }

        var operation = request.SendWebRequest();

        // Wait for the request
        while (!operation.isDone)
            await Task.Yield();

        return request.downloadHandler.text;
    }

    public async Task Callbacks(string path, string contract, RPCValue[] values, RPCCallback[] callbacks)
    {
        string valueString = "{ ";
        for (int i = 0; i < values.Length; i++)
            valueString += "\"" + values[i].name + "\": " + values[i].value + (i == values.Length - 1 ? "" : ", ");
        valueString += " }";

        string callbackString = "{ ";
        for (int i = 0; i < callbacks.Length; i++)
            callbackString += "\"" + callbacks[i].name + "\": true" + (i == callbacks.Length - 1 ? "" : ", ");
        callbackString += " }";

        string bodyString = contract + ", " + valueString + ", " + callbackString;

        var response = await CallAsync(path, bodyString);
        CallbackResponse responseData = JsonUtility.FromJson<CallbackResponse>(response);

        string contractId = responseData.kid;
        string backendStatus = "Kont";
        while (backendStatus == "Kont")
        {
            try
            {
                dynamic result = null;
                int currentFnIdx = GetIndexOfFunction(responseData.m, callbacks);
                if (currentFnIdx == -1)
                    throw new KeyNotFoundException("There is no function named " + responseData.m);
                var currentFn = callbacks[currentFnIdx].value;

                bool isAsync = currentFn is Func<dynamic, Task<dynamic>>;
                string[] args = ExtractArgs(response);      // It is hard to deserialize args without knowing exact types
                                                            // so I did some string work. Could be better

                RPCMultiTyped[] argsMT = new RPCMultiTyped[args.Length];
                for (int i = 0; i < args.Length; i++)
                    argsMT[i] = new RPCMultiTyped(args[i], options);

                result = isAsync
                    ? await currentFn(argsMT)
                    : currentFn(argsMT);

                response = await CallAsync("/kont", "\"" + contractId + "\"", result == null ? "null" : result);

                responseData = JsonUtility.FromJson<CallbackResponse>(response);
                backendStatus = responseData.t;
            }
            catch (Exception e)
            {
                Debug.LogError("Exception while running backend: " + e);
                backendStatus = "Fail";
            }
        }
        Debug.Log("Backend Status: " + backendStatus);
    }

    private string StringifyArgs(object[] args)
    {
        string data = "[";

        for (int i = 0; i < args.Length; i++)
        {
            data = String.Concat(data, args[i], i == args.Length - 1 ? "" : ", ");
        }

        data += "]";

        return data;
    }

    private int GetIndexOfFunction(string fnName, RPCCallback[] list)
    {
        for (int i = 0; i < list.Length; i++)
            if (list[i].name == fnName)
                return i;

        return -1;
    }

    private string[] ExtractArgs(string response)
    {
        string r1 = response.Substring(response.IndexOf("\"args\":") + 7);
        string r2 = r1.Remove(r1.Length - 1, 1);

        return RPCUtils.ExtractArray(r2);
    }
}

public class BypassCertificate : CertificateHandler
{
    protected override bool ValidateCertificate(byte[] certificateData)
    {
        //Simply return true no matter what
        return true;
    }
}

public class RPCUtils
{
    public static string[] ExtractArray(string value)
    {
        string t1 = value.Remove(0, 1);
        string t2 = t1.Remove(t1.Length - 1, 1);
        string t3 = t2.Trim();
        if (t3.Length == 0)
            return null;

        List<string> extArgs = new List<string>();

        string nextString = "";
        List<char> targets = new List<char>() { '\0' };
        int i = 0;
        while (i < t3.Length)
        {
            char currentChar = t3[i];
            /*
                If { or [ add to targets,
                If targets' last remove it
                If no targets and , add it to list
                Otherwise add it to the nextString
            */
            if (currentChar == '{' || currentChar == '[')
            {
                Console.WriteLine("Saw " + currentChar);
                nextString += currentChar;
                targets.Add(currentChar == '{' ? '}' : ']');
                i++;
                continue;
            }
            else if (targets.Count > 1 && currentChar == targets[targets.Count - 1])
            {
                Console.WriteLine("Removing " + currentChar);
                nextString += currentChar;
                targets.RemoveAt(targets.Count - 1);
                i++;
                continue;
            }
            else if (targets.Count == 1 && currentChar == ',')
            {
                Console.WriteLine("Saw , with no target");
                extArgs.Add(nextString.Trim());
                nextString = "";
                i++;
                continue;
            }
            nextString += currentChar;
            i++;
        }
        extArgs.Add(nextString.Trim());

        return extArgs.ToArray();
    }
}