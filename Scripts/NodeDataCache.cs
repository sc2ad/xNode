﻿using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace XNode {
    /// <summary> Precaches reflection data in editor so we won't have to do it runtime </summary>
    public static class NodeDataCache {
        private static PortDataCache portDataCache;
        private static Dictionary<System.Type, Dictionary<string, string>> formerlySerializedAsCache;
        private static bool Initialized { get { return portDataCache != null; } }

        /// <summary> Update static ports and dynamic ports managed by DynamicPortLists to reflect class fields. </summary>
        public static void UpdatePorts(Node node, Dictionary<string, NodePort> ports) {
            if (!Initialized) BuildCache();

            Dictionary<string, NodePort> staticPorts = new Dictionary<string, NodePort>();
            Dictionary<string, List<NodePort>> removedPorts = new Dictionary<string, List<NodePort>>();
            System.Type nodeType = node.GetType();

            Dictionary<string, string> formerlySerializedAs = null;
            if (formerlySerializedAsCache != null) formerlySerializedAsCache.TryGetValue(nodeType, out formerlySerializedAs);

            List<NodePort> dynamicListPorts = new List<NodePort>();

            List<NodePort> typePortCache;
            if (portDataCache.TryGetValue(nodeType, out typePortCache)) {
                for (int i = 0; i < typePortCache.Count; i++) {
                    staticPorts.Add(typePortCache[i].MemberName, portDataCache[nodeType][i]);
                }
            }

            // Cleanup port dict - Remove nonexisting static ports - update static port types
            // AND update dynamic ports (albeit only those in lists) too, in order to enforce proper serialisation.
            // Loop through current node ports
            foreach (NodePort port in ports.Values.ToList()) {
                // If port still exists, check it it has been changed
                NodePort staticPort;
                if (staticPorts.TryGetValue(port.MemberName, out staticPort)) {
                    // If port exists but with wrong settings, remove it. Re-add it later.
                    if (port.IsDynamic || port.direction != staticPort.direction || port.connectionType != staticPort.connectionType || port.typeConstraint != staticPort.typeConstraint) {
                        // If port is not dynamic and direction hasn't changed, add it to the list so we can try reconnecting the ports connections.
                        if (!port.IsDynamic && port.direction == staticPort.direction) removedPorts.Add(port.MemberName, port.GetConnections());
                        port.ClearConnections();
                        ports.Remove(port.MemberName);
                    } else port.ValueType = staticPort.ValueType;
                }
                // If port doesn't exist anymore, remove it
                else if (port.IsStatic) {
                    //See if the field is tagged with FormerlySerializedAs, if so add the port with its new field name to removedPorts
                    // so it can be reconnected in missing ports stage.
                    string newName = null;
                    if (formerlySerializedAs != null && formerlySerializedAs.TryGetValue(port.MemberName, out newName)) removedPorts.Add(newName, port.GetConnections());

                    port.ClearConnections();
                    ports.Remove(port.MemberName);
                }
                // If the port is dynamic and is managed by a dynamic port list, flag it for reference updates
                else if (IsDynamicListPort(port)) {
                    dynamicListPorts.Add(port);
                }
            }
            // Add missing ports
            foreach (NodePort staticPort in staticPorts.Values) {
                if (!ports.ContainsKey(staticPort.MemberName)) {
                    NodePort port = new NodePort(staticPort, node);
                    //If we just removed the port, try re-adding the connections
                    List<NodePort> reconnectConnections;
                    if (removedPorts.TryGetValue(staticPort.MemberName, out reconnectConnections)) {
                        for (int i = 0; i < reconnectConnections.Count; i++) {
                            NodePort connection = reconnectConnections[i];
                            if (connection == null) continue;
                            if (port.CanConnectTo(connection)) port.Connect(connection);
                        }
                    }
                    ports.Add(staticPort.MemberName, port);
                }
            }
            
            // Finally, make sure dynamic list port settings correspond to the settings of their "backing port"
            foreach (NodePort listPort in dynamicListPorts) {
                // At this point we know that ports here are dynamic list ports
                // which have passed name/"backing port" checks, ergo we can proceed more safely.
                string backingPortName = listPort.MemberName.Split(' ')[0];
                NodePort backingPort = staticPorts[backingPortName];
                
                // Update port constraints. Creating a new port instead will break the editor, mandating the need for setters.
                listPort.ValueType = GetBackingValueType(backingPort.ValueType);
                listPort.direction = backingPort.direction;
                listPort.connectionType = backingPort.connectionType;
                listPort.typeConstraint = backingPort.typeConstraint;
            }
        }

        /// <summary>
        /// Extracts the underlying types from arrays and lists, the only collections for dynamic port lists
        /// currently supported. If the given type is not applicable (i.e. if the dynamic list port was not
        /// defined as an array or a list), returns the given type itself. 
        /// </summary>
        private static System.Type GetBackingValueType(System.Type portValType) {
            if (portValType.HasElementType) {
                return portValType.GetElementType();
            }
            if (portValType.IsGenericType && portValType.GetGenericTypeDefinition() == typeof(List<>)) {
                return portValType.GetGenericArguments()[0];
            }
            return portValType;
        }

        /// <summary>Returns true if the given port is in a dynamic port list.</summary>
        private static bool IsDynamicListPort(NodePort port) {
            // Ports flagged as "dynamicPortList = true" end up having a "backing port" and a name with an index, but we have
            // no guarantee that a dynamic port called "output 0" is an element in a list backed by a static "output" port.
            // Thus, we need to check for attributes... (but at least we don't need to look at all fields this time)
            string[] fieldNameParts = port.MemberName.Split(' ');
            if (fieldNameParts.Length != 2) return false;
            
            FieldInfo backingPortInfo = port.node.GetType().GetField(fieldNameParts[0]);
            if (backingPortInfo == null) return false;
            
            object[] attribs = backingPortInfo.GetCustomAttributes(true);
            return attribs.Any(x => {
                Node.InputAttribute inputAttribute = x as Node.InputAttribute;
                Node.OutputAttribute outputAttribute = x as Node.OutputAttribute;
                return inputAttribute != null && inputAttribute.dynamicPortList ||
                       outputAttribute != null && outputAttribute.dynamicPortList;
            });
        }
        
        /// <summary> Cache node types </summary>
        private static void BuildCache() {
            portDataCache = new PortDataCache();
            System.Type baseType = typeof(Node);
            List<System.Type> nodeTypes = new List<System.Type>();
            System.Reflection.Assembly[] assemblies = System.AppDomain.CurrentDomain.GetAssemblies();

            // Loop through assemblies and add node types to list
            foreach (Assembly assembly in assemblies) {
                // Skip certain dlls to improve performance
                string assemblyName = assembly.GetName().Name;
                int index = assemblyName.IndexOf('.');
                if (index != -1) assemblyName = assemblyName.Substring(0, index);
                switch (assemblyName) {
                    // The following assemblies, and sub-assemblies (eg. UnityEngine.UI) are skipped
                    case "UnityEditor":
                    case "UnityEngine":
                    case "System":
                    case "mscorlib":
                    case "Microsoft":
                        continue;
                    default:
                        nodeTypes.AddRange(assembly.GetTypes().Where(t => !t.IsAbstract && baseType.IsAssignableFrom(t)).ToArray());
                        break;
                }
            }

            for (int i = 0; i < nodeTypes.Count; i++) {
                CachePorts(nodeTypes[i]);
            }
        }

        public static List<FieldInfo> GetNodeFields(System.Type nodeType) {
            var fieldInfo = new List<FieldInfo>(nodeType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));

            // GetFields doesnt return inherited private fields, so walk through base types and pick those up
            System.Type tempType = nodeType;
            while ((tempType = tempType.BaseType) != typeof(XNode.Node)) {
                FieldInfo[] parentFields = tempType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
                for (int i = 0; i < parentFields.Length; i++) {
                    // Ensure that we do not already have a member with this type and name
                    FieldInfo parentField = parentFields[i];
                    if (fieldInfo.TrueForAll(x => x.Name != parentField.Name)) {
                        fieldInfo.Add(parentField);
                    }
                }
            }
            return fieldInfo;
        }

        public static List<PropertyInfo> GetNodeProps(System.Type nodeType)
        {
            var props = new List<PropertyInfo>(nodeType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));

            // GetFields doesnt return inherited private fields, so walk through base types and pick those up
            System.Type tempType = nodeType;
            while ((tempType = tempType.BaseType) != typeof(Node))
            {
                var parentProps = tempType.GetProperties(BindingFlags.NonPublic | BindingFlags.Instance);
                for (int i = 0; i < parentProps.Length; i++)
                {
                    // Ensure that we do not already have a member with this type and name
                    var parentProp = parentProps[i];
                    if (props.TrueForAll(x => x.Name != parentProp.Name))
                    {
                        props.Add(parentProp);
                    }
                }
            }
            return props;
        }

        private static void CachePorts<T>(System.Type nodeType, List<T> members) where T : MemberInfo
        {
            foreach (var member in members)
            {
                var attrs = member.GetCustomAttributes(true);
                var inputAttribute = attrs.FirstOrDefault(x => x is Node.InputAttribute) as Node.InputAttribute;
                var outputAttribute = attrs.FirstOrDefault(y => y is Node.OutputAttribute) as Node.OutputAttribute;
                var formerlySerializedAsAttribute = attrs.FirstOrDefault(x => x is UnityEngine.Serialization.FormerlySerializedAsAttribute) as UnityEngine.Serialization.FormerlySerializedAsAttribute;
                if (inputAttribute == null && outputAttribute == null)
                    continue;
                if (inputAttribute != null && outputAttribute != null)
                    Debug.LogError($"Member {member.Name} of type {nodeType.FullName} cannot be both input and output.");
                else
                {
                    if (portDataCache.TryGetValue(nodeType, out var portData))
                    {
                        portData.Add(new NodePort(member));
                    }
                    else
                    {
                        portDataCache.Add(nodeType, new List<NodePort> { new NodePort(member) });
                    }
                }

                if (formerlySerializedAsAttribute != null)
                {
                    if (formerlySerializedAsCache == null)
                        formerlySerializedAsCache = new Dictionary<System.Type, Dictionary<string, string>>();
                    if (formerlySerializedAsCache.TryGetValue(nodeType, out var cacheDict))
                    {
                        Debug.LogError($"Another {nameof(UnityEngine.Serialization.FormerlySerializedAsAttribute)} with value '{formerlySerializedAsAttribute.oldName}' already exist on this node.");
                    }
                    else
                    {
                        formerlySerializedAsCache.Add(nodeType, new Dictionary<string, string>{ { formerlySerializedAsAttribute.oldName, member.Name} });
                    }
                }
            }
        }

        private static void CachePorts(System.Type nodeType) {
            CachePorts(nodeType, GetNodeFields(nodeType));
            CachePorts(nodeType, GetNodeProps(nodeType));
        }

        [System.Serializable]
        private class PortDataCache : Dictionary<System.Type, List<NodePort>>, ISerializationCallbackReceiver {
            [SerializeField] private List<System.Type> keys = new List<System.Type>();
            [SerializeField] private List<List<NodePort>> values = new List<List<NodePort>>();

            // save the dictionary to lists
            public void OnBeforeSerialize() {
                keys.Clear();
                values.Clear();
                foreach (var pair in this) {
                    keys.Add(pair.Key);
                    values.Add(pair.Value);
                }
            }

            // load dictionary from lists
            public void OnAfterDeserialize() {
                this.Clear();

                if (keys.Count != values.Count)
                    throw new System.Exception(string.Format("there are {0} keys and {1} values after deserialization. Make sure that both key and value types are serializable."));

                for (int i = 0; i < keys.Count; i++)
                    this.Add(keys[i], values[i]);
            }
        }
    }
}
