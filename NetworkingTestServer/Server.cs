using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.IO;
using PacketStuff;

namespace NetworkingTestServer
{
    class Server
    {
        //server settings
        public static int roomSize = 2; //the max amount of clients alowed in a room
        public static int TickRate = 30; //the rate at wich the server updates the client handler threads (because of the way the threads are created they must be aborted and restarted consistantly)


        public static string LastReceived = "";
        public static TcpListener server = null;
        public static List<ClientData> clients = new List<ClientData>();
        public static Dictionary<string, ServerRoom> rooms = new Dictionary<string, ServerRoom>();

        public static List<ServerEvent> events = new List<ServerEvent>();

        public static void Main()
        {
            Int32 port = 7777;// Set the TcpListener on port 7777.
            IPAddress localAddr = IPAddress.Parse("10.0.0.4");// Set the TcpListener on IP 10.0.0.4 (my local IP)
            server = new TcpListener(localAddr, port);// TcpListener server = new TcpListener(port);        
            server.Start();// Start listening for client requests.
            
            //get new clients
            Thread addingClientThread = new Thread(new ThreadStart(addClients));
            addingClientThread.Start();


            Console.WriteLine($"Server startup complete...({localAddr}:{port})");

            while (true) 
            {
                Thread.Sleep(1000 / TickRate);
                RunEvents();
                foreach (KeyValuePair<string,ServerRoom> r in rooms) 
                {
                    if (r.Value.Empty) 
                    {
                        rooms.Remove(r.Key);
                    }
                }
            }
        }

        public static void addClients()
        {
            while (true)
            {
                TcpClient newClient = CheckForNewClient();
                ClientData clientData = new ClientData(newClient);
                clientData.clientHandler = new Thread(new ParameterizedThreadStart(ClientHandler));
                clientData.clientHandler.Start(clientData);
                clients.Add(clientData);
            }
        }

        public static TcpClient CheckForNewClient()
        {
            // Perform a blocking call to accept requests.
            // You could also use server.AcceptSocket() here.
            TcpClient client = server.AcceptTcpClient();
            Console.WriteLine("Connected:"+client.Client.RemoteEndPoint.ToString());
            
            return client;
        }

        public static void ClientHandler(Object pClient)
        {
            ClientData client = null;
            try
            {
                client = (ClientData)pClient;
            }
            catch (InvalidCastException)
            {
                Console.WriteLine("Invalid client:This client will be ignored");
                return;
            }
            while (client.client.Connected)
            {

                Byte[] bytes = new Byte[5000];
                try
                {
                    NetworkStream stream = client.client.GetStream();
                    int i;
                    //Loop to receive all the data sent by the client.

                    //FIX This will hang until a packet is recived currently the only way to stop this is to have the client do super fast ping checks
                    //if a client lags out then the server will hang on it untill a packet finaly makes it through
                    while ((i = stream.Read(bytes, 0, bytes.Length)) != 0)
                    {
                        // Translate data bytes to a ASCII string.
                        //Console.WriteLine(BitConverter.ToString(bytes).Replace('-', ' '));

                        string clientIP = client.client.Client.RemoteEndPoint.ToString();
                        DataPacket[] packets = DataProcessor.ProcessInData(bytes);
                        foreach (DataPacket p in packets)
                        {
                            if (p.isEvent)
                            {
                                events.Add(new ServerEvent(p, client));
                            }
                            else
                            {
                                if (rooms.ContainsKey(p.gameID))
                                {
                                    rooms[p.gameID].SendDataToRoom(p, client);
                                }
                            }
                            //search for clients with same game code
                            //relay the packet back with updated client ID value to all other clients belonging to the game code

                        }
                        break;
                    }
                }
                catch (System.IO.IOException)
                {
                    
                    events.Add(new ServerEvent(new DataPacket() { varName = "Disconnect", strData = "Connection may have been closed by client incorectly" }, client));
                    //RemoveClient(client, "Connection may have been closed by client incorectly");
                }
                catch (System.InvalidOperationException)
                {
                    events.Add(new ServerEvent(new DataPacket() { varName = "Disconnect", strData = "Connection may have been closed by client incorectly" }, client));
                    //RemoveClient(client, "Connection may have been closed by client incorectly");
                }

            }
        }

        public static void RemoveClient(ClientData c, string reason) 
        {
            try
            {
                Console.WriteLine($"{c.IP}-Disconnected:{reason}");
                if (c.gameCode != "aaaa")
                {
                    rooms[c.gameCode].RemoveClient(c);
                    if (rooms[c.gameCode].Empty)
                    {
                        rooms.Remove(c.gameCode);
                        Console.WriteLine("Room closed:" + c.gameCode);
                    }
                }
            }
            catch (KeyNotFoundException e) 
            {
                Console.WriteLine("Room already closed:" + c.gameCode);
            }
            c.client.Close();
            clients.Remove(c);
        }

        public static void SendPacket(DataPacket data, ClientData client)
        {
            if (client.client.Connected)
            {
                NetworkStream stream = client.client.GetStream();//get data stream
                byte[] msg = DataProcessor.SerializeDataPacket(data);//convert data packet to bytes
                stream.Write(msg, 0, msg.Length);//send data
            }
            else if (clients.Contains(client)) 
            {
                RemoveClient(client, "Connection may have been closed by client incorectly");
            }
        }

        public static void RunEvents() 
        {
            for (int i = events.Count - 1; i >= 0; i--) 
            {
                try
                {
                    if (events[i] == null) 
                    {
                        continue;
                    }
                    DataPacket p = events[i].data;
                    ClientData client = events[i].client;
                    if (!client.client.Connected) 
                    {
                        if (p.varName == "Disconnect")
                        {
                            RemoveClient(client, p.strData);
                        }
                        events.RemoveAt(i);
                        continue;
                    }
                    if (p.varName == "connectToRoom")
                    {
                        //look for a room avalible with the room code in p.gameID and connect the user to it if avalible
                        if (rooms.ContainsKey(p.strData))
                        {
                            if (!rooms[p.strData].Full)
                            {
                                rooms[p.strData].AddClient(client);
                                client.gameCode = p.strData;
                                SendPacket(new DataPacket() { isEvent = true, varName = "RoomJoin", gameID = p.strData, userID = rooms[p.strData].ClientID(client) }, client);
                                events.RemoveAt(i);
                                continue;
                            }
                        }
                        SendPacket(new DataPacket() { isEvent = true, varName = "RoomJoinFailed" }, client);
                    }
                    else if (p.varName == "newRoom")
                    {
                        //create a new room with a new ID and connect user to the room
                        string newRoomCode = RoomCodeGenerator();
                        ServerRoom room = new ServerRoom();
                        room.AddClient(client);
                        rooms.Add(newRoomCode, room);
                        client.gameCode = newRoomCode;
                        Console.WriteLine("New room:" + newRoomCode);
                        SendPacket(new DataPacket() { isEvent = true, varName = "RoomHost", gameID = newRoomCode, userID = rooms[newRoomCode].ClientID(client) }, client);
                    }
                    else if (p.varName == "serverDisconnect")
                    {
                        RemoveClient(client, "Closed by client");
                    }
                    
                    else if (p.varName == "PingCheck")
                    {
                        SendPacket(new DataPacket() { isEvent = true, varName = "PingCheck" }, client);
                    }

                }
                catch (ObjectDisposedException e)
                {
                    Console.WriteLine(e.Message);
                }
                catch (NullReferenceException e)
                {
                    Console.WriteLine(e.Message);
                }
                events.RemoveAt(i);
            }
        }


        private static string RoomCodeGenerator() 
        {
            Random random = new Random();
            string code = "";
            do
            {
                code = "";
                for (int i = 0; i < 4; i++)
                {
                    code += (char)random.Next(97, 123);
                }
            } while (rooms.ContainsKey(code) || code == "aaaa");
            return code;
        }

    }

    public class ServerRoom 
    {
        List<ClientData> roomClients = new List<ClientData>();
        public bool publicRoom = false;
        public bool Full { get => roomClients.Count == Server.roomSize; }
        public bool Empty { get => roomClients.Count == 0; }



        public void AddClient(ClientData client) 
        {
            
            if (roomClients.Count < Server.roomSize) 
            {
                foreach (ClientData c in roomClients) 
                {
                    Server.SendPacket(new DataPacket() { isEvent = true, varName = "newPlayer", userID = roomClients.Count }, c);
                    Server.SendPacket(new DataPacket() { isEvent = true, varName = "newPlayer", userID = roomClients.IndexOf(c) }, client);
                }
                roomClients.Add(client);

            }
        }

        public void RemoveClient(ClientData client) 
        {
            if (roomClients.Contains(client)) 
            {
                int id = ClientID(client);
                roomClients.Remove(client);
                foreach (ClientData c in roomClients)
                {
                    if (c != client)
                    {
                        Server.SendPacket(new DataPacket() { isEvent = true, varName = "playerLeave", userID = id }, c);
                    }
                }
                
            }

        }

        public int ClientID(ClientData client) 
        {
            return roomClients.IndexOf(client);
        }

        /// <summary>
        /// Sends data to all members of this room except client
        /// </summary>
        public void SendDataToRoom(DataPacket data, ClientData client)
        {
            data.userID = roomClients.IndexOf(client);
            foreach (ClientData c in roomClients)
            {
                if (c != client) 
                {
                    Server.SendPacket(data, c);
                }
            }
        }

        /// <summary>
        /// Sends data to all members of this room 
        /// </summary>
        public void SendDataToRoom(DataPacket data)
        {
            foreach (ClientData c in roomClients)
            {
                Server.SendPacket(data, c);
            }
        }

    }

    public class ClientData 
    {
        public TcpClient client = null;
        public string gameCode = "aaaa";
        public string IP = "";

        public Thread clientHandler;

        public ClientData(TcpClient client) 
        {
            this.client = client;
            IP = client.Client.RemoteEndPoint.ToString();
        }
    }

    public class ServerEvent
    {
        public DataPacket data;
        public ClientData client;

        public ServerEvent(DataPacket data, ClientData client) 
        {
            this.data = data;
            this.client = client;
        }
    }

}
