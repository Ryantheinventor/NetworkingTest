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

                //relay client data
                for (int i = 0; i < clients.Count(); i++) 
                {
                    ClientHandler(clients[i]);
                }
            }
        }

        public static void addClients()
        {
            while (true)
            {
                TcpClient newClient = CheckForNewClient();

                clients.Add(new ClientData(newClient));
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

            
            Byte[] bytes = new Byte[5000];
            try
            {
                NetworkStream stream = client.client.GetStream();
                int i;
                //Loop to receive all the data sent by the client.
                //FIX This will hang until a packet is recived currently the only way to stop this is to have the client do super fast ping checks
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
                            if (p.varName == "connectToRoom")
                            {
                                //look for a room avalible with the room code in p.gameID and connect the user to it if avalible
                                if (rooms.ContainsKey(p.strData))
                                {
                                    if (!rooms[p.strData].Full)
                                    {
                                        rooms[p.strData].addClient(client);
                                        client.gameCode = p.strData;
                                        sendPacket(new DataPacket() { isEvent = true, varName = "RoomJoin", gameID = p.strData, userID = rooms[p.strData].clientID(client) }, client);
                                        break;
                                    }
                                }
                                sendPacket(new DataPacket() { isEvent = true, varName = "RoomJoinFailed" }, client);
                            }
                            else if (p.varName == "newRoom")
                            {
                                //create a new room with a new ID and connect user to the room
                                string newRoomCode = roomCodeGenerator();
                                ServerRoom room = new ServerRoom();
                                room.addClient(client);
                                rooms.Add(newRoomCode, room);
                                client.gameCode = newRoomCode;
                                Console.WriteLine("New room:" + newRoomCode);
                                sendPacket(new DataPacket() { isEvent = true, varName = "RoomHost", gameID = newRoomCode, userID = rooms[newRoomCode].clientID(client) }, client);
                            }
                            else if (p.varName == "serverDisconnect")
                            {
                                removeClient(client, "Closed by client");
                                return;
                            }
                            else if (p.varName == "PingCheck") 
                            {
                                sendPacket(new DataPacket() { isEvent = true, varName = "PingCheck" }, client);
                                continue;
                            }
                            else if (p.gameID != "aaaa")
                            {
                                //if the event is not recognised by the server it is passed along as a game event
                                rooms[p.gameID].sendDataToRoom(p, client);
                            }
                        }
                        else
                        {
                            if (rooms.ContainsKey(p.gameID))
                            {
                                rooms[p.gameID].sendDataToRoom(p, client);
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
                removeClient(client, "Connection may have been closed by client incorectly");
            }
            catch (System.InvalidOperationException)
            {
                removeClient(client, "Connection may have been closed by client incorectly");
            }


        }

        public static void removeClient(ClientData c, string reason) 
        {
            Console.WriteLine($"{c.IP}-Disconnected:{reason}");
            if (c.gameCode != "aaaa")
            {
                rooms[c.gameCode].removeClient(c);
                if (rooms[c.gameCode].Empty) 
                {
                    rooms.Remove(c.gameCode);
                }
            }
            
            c.client.Close();
            clients.Remove(c);
        }

        public static void sendPacket(DataPacket data, ClientData client)
        {
            NetworkStream stream = client.client.GetStream();//get data stream
            byte[] msg = DataProcessor.SerializeDataPacket(data);//convert data packet to bytes
            stream.Write(msg, 0, msg.Length);//send data
        }

        private static string roomCodeGenerator() 
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



        public void addClient(ClientData client) 
        {
            
            if (roomClients.Count < Server.roomSize) 
            {
                foreach (ClientData c in roomClients) 
                {
                    Server.sendPacket(new DataPacket() { isEvent = true, varName = "newPlayer", userID = roomClients.Count }, c);
                    Server.sendPacket(new DataPacket() { isEvent = true, varName = "newPlayer", userID = roomClients.IndexOf(c) }, client);
                }
                roomClients.Add(client);

            }
        }

        public void removeClient(ClientData client) 
        {
            if (roomClients.Contains(client)) 
            {
                int id = clientID(client);
                roomClients.Remove(client);
                foreach (ClientData c in roomClients)
                {
                    if (c != client)
                    {
                        Server.sendPacket(new DataPacket() { isEvent = true, varName = "playerLeave", userID = id }, c);
                    }
                }
                
            }

        }

        public int clientID(ClientData client) 
        {
            return roomClients.IndexOf(client);
        }

        //TODO add remove member method
        //should remove member from the room and send an event to all other members about the disconnection
        //if the room would end up being empty it should remove itself from the rooms dictionary


        /// <summary>
        /// Sends data to all members of this room except client
        /// </summary>
        public void sendDataToRoom(DataPacket data, ClientData client)
        {
            data.userID = roomClients.IndexOf(client);
            foreach (ClientData c in roomClients)
            {
                if (c != client) 
                {
                    Server.sendPacket(data, c);
                }
            }
        }

        /// <summary>
        /// Sends data to all members of this room 
        /// </summary>
        public void sendDataToRoom(DataPacket data)
        {
            foreach (ClientData c in roomClients)
            {
                Server.sendPacket(data, c);
            }
        }

    }

    public class ClientData 
    {
        public TcpClient client = null;
        public Dictionary<string, NetworkingVector3> vectors = new Dictionary<string, NetworkingVector3>();
        public Dictionary<string, string> strings = new Dictionary<string, string>();
        public Dictionary<string, int> ints = new Dictionary<string, int>();
        public Dictionary<string, bool> bools = new Dictionary<string, bool>();
        public Dictionary<string, float> floats = new Dictionary<string, float>();
        public Dictionary<string, char> chars = new Dictionary<string, char>();
        public string gameCode = "aaaa";
        public string IP = "";
        public ClientData(TcpClient client) 
        {
            this.client = client;
            this.IP = client.Client.RemoteEndPoint.ToString();
        }
    }

}
