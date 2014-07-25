﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using NLua;

namespace Selene
{
    public class SeleneProcess
    {
        public class LogEntry
        {
            public string message;
            public int priority;
            public SeleneCallback callback;
            public LogEntry(SeleneCallback Callback, string Message, int Prio)
            {
                callback = Callback;
                message = Message;
                priority = Prio;
            }

        }


        private int priority = 0;

        public int Priority
        {
            get { return priority; }
            set
            {
                priority = value;
                if (Parent != null)
                {
                    Parent.Children = Parent.Children.OrderBy(proc => proc.priority).ToList();
                }
            }
        }

        public SeleneProcess Parent = null;
        public List<SeleneProcess> Children = new List<SeleneProcess>();

        public List<List<SeleneCallback>> Callbacks = new List<List<SeleneCallback>>();
        SeleneLuaState luaState;
        LuaTable environment;

        public HashSet<string> persistentVariables = new HashSet<string>();


        public LinkedList<LogEntry> LogList = new LinkedList<LogEntry>();

        public LuaTable Env
        {
            get { return environment; }
        }


        public bool IsCustomVariable(object key)
        {
            if (luaState.BaseEnvironment[key] == null)
            {
                return true;
            }
            return false;
        }

        public LuaTable CustomEnvironment
        {
            get
            {
                LuaTable toReturn = luaState.GetNewTable();
                LuaTable myEnv = Env;
                foreach (object key in myEnv.Keys)
                {
                    if (luaState.BaseEnvironment[key] == null)
                    {
                        toReturn[key] = myEnv[key];
                    }
                }
                return toReturn;
            }
        }

        SeleneCallback currentCallback;
        public string Name = "";
        public string path = "";
        public string source = "";
        bool fromFile = false;
        bool run = false;

        public bool toDelete = false;

        public bool Active
        {
            get { return run; }
            set { run = value; }
        }

        public bool IsActive
        {
            get
            {
                if (!Active)
                {
                    return false;
                }
                if (Parent != null)
                {
                    return Parent.IsActive;
                }
                return Active;
            }
        }

        public void AddChildProcess(SeleneProcess proc)
        {
            Children.Add(proc);
            Children = Children.OrderBy(child => child.Priority).ToList();
            proc.Parent = this;
        }

        public void AddCallback(CallbackType type, LuaFunction callback)
        {
            Callbacks[(int)type].Add(new SeleneCallback(callback, luaState, type));
        }

        public SeleneProcess(SeleneLuaState state)
        {
            luaState = state;
            foreach (var type in Enum.GetValues(typeof(CallbackType)))
            {
                Callbacks.Add(new List<SeleneCallback>());
            }
        }

        public bool LoadFromFile(string filepath)
        {
            path = filepath;
            Name = path;
            source = ((ILuaDataProvider)luaState).ReadFile(path);
            fromFile = true;

            LoadSource();
            return true;
        }

        public bool LoadFromString(string newSource, string sourceName)
        {
            fromFile = false;
            path = "";
            Name = sourceName;
            source = newSource;

            LoadSource();
            return true;
        }

        public bool RunString(string src, string name)
        {
            luaState.CurrentProcess = this;
            try
            {
                LuaFunction runInEnv = (LuaFunction) luaState.LuaState.LoadString(@"
                    local args = {...}
                    return load(args[1],args[2], 'bt',args[3]);  
                ","Enviroment Runner");
                object[] ret = runInEnv.Call(src, name, environment);
                if (ret[0] is LuaFunction)
                {
                    ((LuaFunction) ret[0]).Call();
                }
                else
                {
                    luaState.Log(string.Format("Lua Error in {0}:\n{1} ", name.ToString(), ret[1]));
                    luaState.revertCallStack();
                    return false;
                }
            }
            catch (NLua.Exceptions.LuaException ex)
            {
                luaState.Log(string.Format("Lua Error in {0}:\n{1} ", name.ToString(), ex.Message));
                luaState.revertCallStack();
                return false;
            }
            luaState.revertCallStack();
            return true;
        }

        private bool LoadSource()
        {
            LogList.Clear();

            List<SeleneProcess> toDelete = new List<SeleneProcess>(Children);
            toDelete.ForEach(child => child.Delete());

            foreach (var callbackList in Callbacks)
            {
                callbackList.Clear();
            }
            environment = luaState.GetNewEnvironment();
            return RunString(source, path);
        }

        public bool Reload()
        {
            if (fromFile)
            {
                return LoadFromFile(path);
            }
            else
            {
                return LoadFromString(source, path);
            }
        }

        public void Delete()
        {
            foreach (var proc in Children)
            {
                proc.Delete();
            }
            if (this.Parent != null)
            {
                Parent.Children.Remove(this);
                this.Parent = null;
            }
        }


        public void Execute(CallbackType type, params object[] parameters)
        {
            if (run && luaState.GetExecutingVessel() != null)
            {
                foreach (var child in Children)
                {
                    child.Execute(type, parameters);
                }
                luaState.CurrentProcess = this;
                foreach (var callback in Callbacks[(int)type])
                {
                    currentCallback = callback;
                    if (!callback.Execute(parameters))
                    {
                        run = false;
                        luaState.revertCallStack();
                        return;
                    }
                }
                luaState.revertCallStack();
            }
        }

        public void Log(string message, int priority)
        {
            LogList.AddLast(new LogEntry(this.currentCallback, message, priority));
        }

        public SeleneProcess CreateChildProcess()
        {
            var proc = new SeleneProcess(luaState);
            AddChildProcess(proc);
            return proc;
        }
        private static string EncodeString(string val)
        {
            return Convert.ToBase64String(Encoding.Unicode.GetBytes(val)).Replace('=','-');
        }
        private static string DecodeString(string val)
        {
            return Encoding.Unicode.GetString(Convert.FromBase64String(val.Replace('-','=')));
        }

        public void SaveObject(ConfigNode saveInto)
        {
            ConfigNode proc = new ConfigNode("Process");
            saveInto.AddNode(proc);
            proc.AddValue("Name", EncodeString(path));
            proc.AddValue("Active", Active);
            if (!fromFile)
            {
                proc.AddValue("Source", EncodeString(source));
            }
            proc.AddValue("FromFile", fromFile);

            ConfigNode varsNode = new ConfigNode("Variables");
            proc.AddNode(varsNode);     

            foreach (string varName in persistentVariables)
            {
                ConfigNode varNode = new ConfigNode(EncodeString(varName));
                if (SaveObject(varNode, environment[varName], new HashSet<string>()))
                {
                    varsNode.AddNode(varNode);
                }
            }
        }

        private bool SaveObject(ConfigNode saveInto, object val, HashSet<string> savedTables)
        {
            if (val != null)
            {
                ConfigNode newNode = new ConfigNode();
                if (val is double)
                {
                    newNode.name = "Double";
                    newNode.AddValue("Value", val.ToString());
                }
                else if (val is String)
                {
                    newNode.name = "String";
                    newNode.AddValue("Value", EncodeString(val.ToString()));
                }
                else if (val is Boolean)
                {
                    newNode.name = "Boolean";
                    newNode.AddValue("Value", val.ToString());
                }
                else if (val is LuaTable)
                {
                    LuaTable tab = (LuaTable)val;
                    string tableName = luaState.LuaToString(tab);
                    if (savedTables.Contains(tableName))
                    {
                        return false;
                    }
                    savedTables.Add(tableName);
                    
                    newNode.name = "Table";
                    foreach (object key in tab.Keys)
                    {
                        ConfigNode entry = new ConfigNode("Entry");
                        if (SaveObject(entry, key, savedTables) && SaveObject(entry, tab[key], savedTables))
                        {
                            newNode.AddNode(entry);
                        }
                    }
                    savedTables.Remove(tableName);
                }
                else if (val is Vector3d)
                {
                    Vector3d vec = (Vector3d)val;
                    newNode.name = "Vector";
                    newNode.AddValue("X", vec.x);
                    newNode.AddValue("Y", vec.y);
                    newNode.AddValue("Z", vec.z);
                }
                else if (val is QuaternionD)
                {
                    QuaternionD quat = (QuaternionD)val;
                    newNode.name = "Quaternion";
                    newNode.AddValue("X", quat.x);
                    newNode.AddValue("Y", quat.y);
                    newNode.AddValue("Z", quat.z);
                    newNode.AddValue("W", quat.w);

                }
                else
                {
                    return false;
                }
                saveInto.AddNode(newNode);
                return true;
            }
            return false;
        }

        public void LoadState(ConfigNode loadFrom)
        {
            if (loadFrom != null)
            {
                path = DecodeString(loadFrom.GetValue("Name"));
                Active = Boolean.Parse(loadFrom.GetValue("Active"));
                fromFile = Boolean.Parse(loadFrom.GetValue("FromFile"));
                if (loadFrom.HasValue("Source"))
                {
                    string val = loadFrom.GetValue("Source");

                    source = DecodeString(val);
                }

                Reload();
                foreach (ConfigNode varNode in loadFrom.GetNode("Variables").nodes)
                {
                    string key = DecodeString(varNode.name);
                    object value = LoadObject(varNode.nodes[0]);
                    Env[key] = value;
                }
            }            
        }

        private object LoadObject(ConfigNode loadFrom)
        {
            switch(loadFrom.name)
            {
                case "Double":
                    return Double.Parse(loadFrom.GetValue("Value"));
                case "String":
                    return DecodeString(loadFrom.GetValue("Value"));
                case "Boolean":
                    return Boolean.Parse(loadFrom.GetValue("Value"));
                case "Vector":
                    return new Vector3d(
                        Double.Parse(loadFrom.GetValue("X")),
                        Double.Parse(loadFrom.GetValue("Y")),
                        Double.Parse(loadFrom.GetValue("Z")));
                case "Quaternion":
                    return new QuaternionD(
                        Double.Parse(loadFrom.GetValue("X")),
                        Double.Parse(loadFrom.GetValue("Y")),
                        Double.Parse(loadFrom.GetValue("Z")),
                        Double.Parse(loadFrom.GetValue("W")));
                case "Table":
                    LuaTable tab = luaState.GetNewTable();
                    foreach(var entry in loadFrom.GetNodes("Entry"))
                    {
                        if(entry.nodes.Count == 2)
                        {
                            object key = LoadObject(entry.nodes[0]);
                            object value = LoadObject(entry.nodes[1]);
                            tab[key] = value;
                        }
                    }
                    return tab;
                default:
                    return null;
            }
        }

        public void Persist(string varName)
        {
            persistentVariables.Add(varName);
        }
    }
}
