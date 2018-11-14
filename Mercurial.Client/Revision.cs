// 
//  Revision.cs
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
using System.IO;
using System.Xml;
using System.Xml.Linq;
using System.Collections.Generic;
using System.Linq;

namespace Mercurial.Client
{
    /// <summary>
    /// Represents a mercurial revision
    /// </summary>
    public class Revision
    {
        /// <summary>
        /// The revision number
        /// </summary>
        public string RevisionId { get; private set; }

        /// <summary>
        /// The date the revision was created
        /// </summary>
        public DateTime Date { get; private set; }

        /// <summary>
        /// The commit message for the revision
        /// </summary>
        public string Message { get; private set; }

        public string AuthorName { get; }

        public string AuthorEmail { get; }

        public string Hash { get; set; }

        public string Branch { get; set; }

        internal Revision(XElement node)
        {
            RevisionId = node.Attribute("revision").Value;
            Date = DateTime.Parse(node.Element("date").Value);
            AuthorEmail = node.Element("author").Attribute("email").Value;
            AuthorName = node.Element("author").Value;
            Message = node.Element("msg").Value;
            Hash = node.Attribute("node").Value;
            Branch = node.Element("branch")?.Value
                ?? node.Elements("extra").FirstOrDefault(q => q.Attribute("key").Value.Equals("branch", StringComparison.OrdinalIgnoreCase))?.Value;
        }


        /// <summary>
        /// Parse an xml log into a list of revisions
        /// </summary>
        internal static IList<Revision> ParseRevisionsFromLog(string output)
        {
            var revisions = new List<Revision>();
            if (String.IsNullOrWhiteSpace(output))
                return revisions;

            var index = output.IndexOf(@"<?xml", StringComparison.CurrentCulture);
            if (index < 0)
                throw new XmlException("Invalid xml");

            using (var reader = new StringReader(output.Substring(index)))
            {
                var document = XDocument.Load(reader);

                foreach (var node in document.Descendants("log").Descendants("logentry"))
                    revisions.Add(new Revision(node));

                return revisions;
            }
        }
    }
}
