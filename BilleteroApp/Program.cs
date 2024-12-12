using System;
using System.IO.Ports;
using System.Net.Http;
using System.Threading.Tasks;
using ITLlib; 

class NV10CounterExample
{
    static bool portOpen = false; 
    static int totalMoney = 0;    
    static int count20 = 0;       
    static int count50 = 0;       
    static int count100 = 0;      
    static int count200 = 0;

    // Diccionario para mapear el canal con el valor del billete
    static System.Collections.Generic.Dictionary<byte, int> valorPorCanal =
        new System.Collections.Generic.Dictionary<byte, int>()
        {
            {1, 20},
            {2, 50},
            {3, 100},
            {4, 200}
        };

    static async Task Main(string[] args)
    {
        SSPComms ssp = new SSPComms();
        SSP_COMMAND cmd = new SSP_COMMAND();
        SSP_COMMAND_INFO cmdInfo = new SSP_COMMAND_INFO();
        SSP_KEYS sspKeys = new SSP_KEYS();

        cmd.ComPort = "COM8";   // Ajustar según tu entorno
        cmd.BaudRate = 9600;
        cmd.Timeout = 1000;
        cmd.RetryLevel = 3;
        cmd.SSPAddress = 0x00; 
        cmd.EncryptionStatus = false;

        if (!ssp.OpenSSPComPort(cmd))
        {
            Console.WriteLine("No se pudo abrir el puerto.");
            return;
        }
        else
        {
            portOpen = true;
        }

        // SYNC
        cmd.CommandDataLength = 1;
        cmd.CommandData[0] = 0x11; // SYNC
        if (!TransmitCommand(ssp, cmd, cmdInfo))
        {
            Console.WriteLine("Fallo al hacer SYNC");
            return;
        }

        // Negociar llaves
        if (!ssp.InitiateSSPHostKeys(sspKeys, cmd))
        {
            Console.WriteLine("Fallo al iniciar llaves del host");
            return;
        }

        // Set Generator
        cmd.CommandDataLength = 9;
        cmd.CommandData[0] = 0x4A;
        WriteUInt64ToCmd(sspKeys.Generator, cmd.CommandData, 1);
        if (!TransmitCommand(ssp, cmd, cmdInfo))
        {
            Console.WriteLine("Fallo al enviar Generator");
            return;
        }

        // Set Modulus
        cmd.CommandDataLength = 9;
        cmd.CommandData[0] = 0x4B; 
        WriteUInt64ToCmd(sspKeys.Modulus, cmd.CommandData, 1);
        if (!TransmitCommand(ssp, cmd, cmdInfo))
        {
            Console.WriteLine("Fallo al enviar Modulus");
            return;
        }

        // Key Exchange
        cmd.CommandDataLength = 9;
        cmd.CommandData[0] = 0x4C; 
        WriteUInt64ToCmd(sspKeys.HostInter, cmd.CommandData, 1);
        if (!TransmitCommand(ssp, cmd, cmdInfo))
        {
            Console.WriteLine("Fallo al Request Key Exchange");
            return;
        }

        UInt64 slaveInterKey = ReadUInt64FromResponse(cmd.ResponseData, 1);
        sspKeys.SlaveInterKey = slaveInterKey;

        if (!ssp.CreateSSPHostEncryptionKey(sspKeys))
        {
            Console.WriteLine("Fallo al crear llave de encriptación");
            return;
        }

        cmd.Key.FixedKey = 0x0123456701234567;
        cmd.Key.VariableKey = sspKeys.KeyHost;
        cmd.EncryptionStatus = true;

        // Set Protocol Version (6)
        cmd.CommandDataLength = 2;
        cmd.CommandData[0] = 0x06;
        cmd.CommandData[1] = 0x06;
        if (!TransmitCommand(ssp, cmd, cmdInfo))
        {
            Console.WriteLine("Fallo al fijar protocolo");
            return;
        }

        // Setup Request
        cmd.CommandDataLength = 1;
        cmd.CommandData[0] = 0x05; 
        if (!TransmitCommand(ssp, cmd, cmdInfo))
        {
            Console.WriteLine("Fallo al Setup Request");
            return;
        }

        // Set Inhibits
        cmd.CommandDataLength = 3;
        cmd.CommandData[0] = 0x02;
        cmd.CommandData[1] = 0xFF; 
        cmd.CommandData[2] = 0xFF; 
        if (!TransmitCommand(ssp, cmd, cmdInfo))
        {
            Console.WriteLine("Fallo al Set Inhibits");
            return;
        }

        // Enable
        cmd.CommandDataLength = 1;
        cmd.CommandData[0] = 0x0A; 
        if (!TransmitCommand(ssp, cmd, cmdInfo))
        {
            Console.WriteLine("Fallo al habilitar validador");
            return;
        }

        Console.WriteLine("Validador habilitado. Esperando billetes...");
        Console.WriteLine("Escribe 'apagar' para deshabilitar y salir.");

        while (true)
        {
            // Check user input
            if (Console.KeyAvailable)
            {
                string userInput = Console.ReadLine() ?? string.Empty;
                if (userInput.Trim().ToLower() == "apagar")
                {
                    Apagar(ssp, cmd, cmdInfo);
                    break;
                }
            }

            System.Threading.Thread.Sleep(200);
            cmd.CommandDataLength = 1;
            cmd.CommandData[0] = 0x07; // Poll
            if (!TransmitCommand(ssp, cmd, cmdInfo))
            {
                Console.WriteLine("Fallo en Poll");
                break;
            }

            if (cmd.ResponseData[0] == 0xF0)
            {
                for (int i = 1; i < cmd.ResponseDataLength; i++)
                {
                    byte evt = cmd.ResponseData[i];

                    if (evt == 0xEE) // Note Credit
                    {
                        i++;
                        byte channel = cmd.ResponseData[i];
                        int valorNota = 0;
                        if (valorPorCanal.ContainsKey(channel))
                            valorNota = valorPorCanal[channel];

                        totalMoney += valorNota;

                        // Incrementar el contador según el valor
                        switch (valorNota)
                        {
                            case 20:
                                count20++;
                                break;
                            case 50:
                                count50++;
                                break;
                            case 100:
                                count100++;
                                break;
                            case 200:
                                count200++;
                                break;
                        }

                        // Mostrar las 5 cosas:
                        Console.WriteLine("Total: {0}", totalMoney);
                        Console.WriteLine("Billetes de 20: {0}", count20);
                        Console.WriteLine("Billetes de 50: {0}", count50);
                        Console.WriteLine("Billetes de 100: {0}", count100);
                        Console.WriteLine("Billetes de 200: {0}", count200);

                        // Enviar a la API 
                        await EnviarDatosApi(totalMoney);
                    }
                    else
                    {
                        // Otros eventos, no imprimir
                    }
                }
            }
        }

        if (portOpen)
        {
            Apagar(ssp, cmd, cmdInfo);
        }
    }

    static bool TransmitCommand(SSPComms ssp, SSP_COMMAND cmd, SSP_COMMAND_INFO info)
    {
        if (!ssp.SSPSendCommand(cmd, info))
            return false;
        if (cmd.ResponseStatus != PORT_STATUS.SSP_REPLY_OK)
            return false;
        return true;
    }

    static void WriteUInt64ToCmd(UInt64 value, byte[] data, int offset)
    {
        for (int i = 0; i < 8; i++)
        {
            data[offset + i] = (byte)((value >> (8 * i)) & 0xFF);
        }
    }

    static UInt64 ReadUInt64FromResponse(byte[] resp, int offset)
    {
        UInt64 val = 0;
        for (int i = 0; i < 8; i++)
        {
            val += (UInt64)resp[offset + i] << (8 * i);
        }
        return val;
    }

    static void Apagar(SSPComms ssp, SSP_COMMAND cmd, SSP_COMMAND_INFO info)
    {
        cmd.CommandDataLength = 1;
        cmd.CommandData[0] = 0x09; // Disable command
        if (!TransmitCommand(ssp, cmd, info))
        {
            Console.WriteLine("Fallo al deshabilitar validador");
        }
        else
        {
            Console.WriteLine("Validador deshabilitado.");
        }

        ssp.CloseComPort();
        portOpen = false;
        Console.WriteLine("Puerto cerrado. Apagado completo.");

        Console.WriteLine("----- RESUMEN FINAL -----");
        Console.WriteLine("Total: {0}", totalMoney);
        Console.WriteLine("Billetes de 20: {0}", count20);
        Console.WriteLine("Billetes de 50: {0}", count50);
        Console.WriteLine("Billetes de 100: {0}", count100);
        Console.WriteLine("Billetes de 200: {0}", count200);
    }

    static async Task EnviarDatosApi(int total)
    {
        using (HttpClient client = new HttpClient())
        {
            // Se envía un POST a http://iot-sintron.com:3000/update
            var content = new FormUrlEncodedContent(new[]
            {
                new System.Collections.Generic.KeyValuePair<string,string>("api_key", ""),
                new System.Collections.Generic.KeyValuePair<string,string>("field1", total.ToString())
            });

            try
            {
                HttpResponseMessage response = await client.PostAsync("http://iot-sintron.com:3000/update", content);
                string responseString = await response.Content.ReadAsStringAsync();
                if (responseString == "0")
                {
                    Console.WriteLine("Actualización a la API falló.");
                }
                else
                {
                    Console.WriteLine("Actualización a la API exitosa, ID entrada: " + responseString);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error al enviar a la API: " + ex.Message);
            }
        }
    }
}
