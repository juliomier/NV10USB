using System;
using System.IO.Ports;
using ITLlib; // Asegúrate de tener la referencia a tu DLL y clases que ya posees

class NV10CounterExample
{
    static void Main(string[] args)
    {
        SSPComms ssp = new SSPComms();
        SSP_COMMAND cmd = new SSP_COMMAND();
        SSP_COMMAND_INFO cmdInfo = new SSP_COMMAND_INFO();
        SSP_KEYS sspKeys = new SSP_KEYS();

        // Configuración del comando: Ajusta el puerto y demás parámetros
        cmd.ComPort = "COM8";        // Ajustar según tu entorno
        cmd.BaudRate = 9600;
        cmd.Timeout = 1000;
        cmd.RetryLevel = 3;
        cmd.SSPAddress = 0x00; // NV10 suele ser address 0
        cmd.EncryptionStatus = false;

        // Abrir el puerto
        if (!ssp.OpenSSPComPort(cmd))
        {
            Console.WriteLine("No se pudo abrir el puerto.");
            return;
        }

        // 1. SYNC
        cmd.CommandDataLength = 1;
        cmd.CommandData[0] = 0x11; // SYNC
        if (!TransmitCommand(ssp, cmd, cmdInfo))
        {
            Console.WriteLine("Fallo al hacer SYNC");
            return;
        }

        // 2. Negociar claves (Generator, Modulus, KeyExchange)
        // Suponemos que InitiateSSPHostKeys retorna bool
        if (!ssp.InitiateSSPHostKeys(sspKeys, cmd))
        {
            Console.WriteLine("Fallo al iniciar llaves del host");
            return;
        }

        // Enviar Generator
        cmd.CommandDataLength = 9;
        cmd.CommandData[0] = 0x4A; // Set Generator
        WriteUInt64ToCmd(sspKeys.Generator, cmd.CommandData, 1);
        if (!TransmitCommand(ssp, cmd, cmdInfo))
        {
            Console.WriteLine("Fallo al enviar Generator");
            return;
        }

        // Enviar Modulus
        cmd.CommandDataLength = 9;
        cmd.CommandData[0] = 0x4B; // Set Modulus
        WriteUInt64ToCmd(sspKeys.Modulus, cmd.CommandData, 1);
        if (!TransmitCommand(ssp, cmd, cmdInfo))
        {
            Console.WriteLine("Fallo al enviar Modulus");
            return;
        }

        // Enviar Host Inter Key
        cmd.CommandDataLength = 9;
        cmd.CommandData[0] = 0x4C; // Request Key Exchange
        WriteUInt64ToCmd(sspKeys.HostInter, cmd.CommandData, 1);
        if (!TransmitCommand(ssp, cmd, cmdInfo))
        {
            Console.WriteLine("Fallo al Request Key Exchange");
            return;
        }

        // Leer SlaveInterKey
        UInt64 slaveInterKey = ReadUInt64FromResponse(cmd.ResponseData, 1);

        sspKeys.SlaveInterKey = slaveInterKey;
        // Suponemos que CreateSSPHostEncryptionKey retorna bool
        if (!ssp.CreateSSPHostEncryptionKey(sspKeys))
        {
            Console.WriteLine("Fallo al crear llave de encriptación");
            return;
        }

        // Fijar llaves en cmd (cambiamos EncryptKey por VariableKey)
        cmd.Key.FixedKey = 0x0123456701234567; // Ajustar si es necesario
        cmd.Key.VariableKey = sspKeys.KeyHost;
        cmd.EncryptionStatus = true; // habilitamos encriptación

        // 3. Set Host Protocol Version (ej: 6)
        cmd.CommandDataLength = 2;
        cmd.CommandData[0] = 0x06; // Host Protocol Version
        cmd.CommandData[1] = 0x06; // Protocolo version 6 (ejemplo)
        if (!TransmitCommand(ssp, cmd, cmdInfo))
        {
            Console.WriteLine("Fallo al fijar protocolo");
            return;
        }

        // 4. Setup Request
        cmd.CommandDataLength = 1;
        cmd.CommandData[0] = 0x05; // Setup Request
        if (!TransmitCommand(ssp, cmd, cmdInfo))
        {
            Console.WriteLine("Fallo al Setup Request");
            return;
        }

        byte numberOfChannels = cmd.ResponseData[11];
        int[] channelValues = ParseChannelValues(cmd.ResponseData, numberOfChannels);

        // 5. Set Inhibits: habilitar todos los canales (ejemplo)
        cmd.CommandDataLength = 3;
        cmd.CommandData[0] = 0x02; // Set Inhibits
        cmd.CommandData[1] = 0xFF; // canales 1-8 habilitados
        cmd.CommandData[2] = 0xFF; // canales 9-16 habilitados
        if (!TransmitCommand(ssp, cmd, cmdInfo))
        {
            Console.WriteLine("Fallo al Set Inhibits");
            return;
        }

        // 6. Enable
        cmd.CommandDataLength = 1;
        cmd.CommandData[0] = 0x0A; // Enable
        if (!TransmitCommand(ssp, cmd, cmdInfo))
        {
            Console.WriteLine("Fallo al habilitar validador");
            return;
        }

        Console.WriteLine("Validador habilitado. Esperando billetes...");

        int totalMoney = 0;
        // 7. Poll loop
        while (true)
        {
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
                        if (channel <= numberOfChannels && channel > 0)
                        {
                            valorNota = channelValues[channel - 1];
                        }
                        totalMoney += valorNota;
                        Console.WriteLine("Billete aceptado: Canal {0}, Valor: {1} - Total: {2}", channel, valorNota, totalMoney);
                    }
                    else
                    {
                        // Otros eventos si son necesarios
                    }
                }
            }
        }

        ssp.CloseComPort();
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

    static int[] ParseChannelValues(byte[] resp, byte numberOfChannels)
    {
        // Esta función depende del protocolo. Aquí simplificado asumiendo protocolo >=6
        int[] values = new int[numberOfChannels];
        int baseOffset = 16 + (numberOfChannels * 5);

        for (int i = 0; i < numberOfChannels; i++)
        {
            int chanOffset = baseOffset + (i * 4);
            int val = resp[chanOffset] + (resp[chanOffset + 1] << 8) + (resp[chanOffset + 2] << 16) + (resp[chanOffset + 3] << 24);
            values[i] = val;
        }

        return values;
    }
}
