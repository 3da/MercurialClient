// 
//  CommandMessage.cs
//  
//  Author:
//       Levi Bard <levi@unity3d.com>
//  
//  Copyright (c) 2011 Levi Bard
// 
//  This program is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Text;

namespace Mercurial.Client
{
	/// <summary>
	/// Represents a message from the command server
	/// </summary>
	internal class CommandMessage
	{
		/// <summary>
		/// The message's command channel
		/// </summary>
		public CommandChannel Channel { get; private set; }

		/// <summary>
		/// The raw message buffer
		/// </summary>
		public byte[] Buffer { 
			get { return _buffer; }
		}

		/// <summary>
		/// The string representation of the message
		/// </summary>
		public string Message {
			get {
				if (null != _message) return _message;
				return _message = new string (UTF8Encoding.UTF8.GetChars (Buffer));
			}
		}
		
		string _message;
		byte[] _buffer;
		
		public CommandMessage (CommandChannel channel, byte[] buffer)
		{
			Channel = channel;
			_buffer = buffer;
		}
		
		public CommandMessage (CommandChannel channel, string message)
		{
			Channel = channel;
			_message = message;
		}
	}
}

